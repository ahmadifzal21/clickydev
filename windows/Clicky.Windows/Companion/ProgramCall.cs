using System.Globalization;

namespace Clicky.Windows.Companion;

/// <summary>
/// One statement of a reply program, such as <c>say("open the file menu")</c> or <c>ring(120, 40, 18)</c>. Arguments are
/// strings, doubles, arrays (<c>object?[]</c>), objects (<c>Dictionary&lt;string, object?&gt;</c>), bools or null. The model's
/// text is only ever read as data like this, never run.
/// </summary>
/// <param name="ScreenNumber">1-based screen the call's coordinates are on (set by an earlier <c>screen(n)</c>), or null for the cursor's screen.</param>
internal sealed record ProgramCall(string Name, IReadOnlyList<object?> Arguments, int? ScreenNumber = null)
{
    /// <summary>A <c>say</c> made from a later sentence of the same prose line: its marks belong to that line's first sentence.</summary>
    public bool ContinuesLine { get; init; }

    public string? Text(int index) => index < Arguments.Count ? Arguments[index] switch
    {
        string text => text,
        double number => number.ToString(CultureInfo.InvariantCulture),
        _ => null
    } : null;

    public double? Number(int index) => index < Arguments.Count ? Arguments[index] switch
    {
        double number => number,
        string text when double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) => parsed,
        _ => null
    } : null;

    /// <summary>A flat list of numbers given as an array argument, e.g. trace's <c>[x1, y1, x2, y2, ...]</c> (nested pairs are flattened).</summary>
    public double[]? Numbers(int index)
    {
        if (index >= Arguments.Count || Arguments[index] is not object?[] array) return null;
        List<double> numbers = [];
        void Flatten(object?[] items)
        {
            foreach (object? item in items)
            {
                if (item is double number) numbers.Add(number);
                else if (item is object?[] inner) Flatten(inner);
            }
        }
        Flatten(array);
        return numbers.ToArray();
    }

    /// <summary>All number arguments in order, starting at <paramref name="startIndex"/>, until the first non-number.</summary>
    public double[] LeadingNumbers(int startIndex = 0)
    {
        List<double> numbers = [];
        for (int index = startIndex; index < Arguments.Count && Number(index) is { } number; index++) numbers.Add(number);
        return numbers.ToArray();
    }

    /// <summary>The first string argument after the numbers: the label of <c>point(x, y, "label")</c> or <c>click(x1, y1, x2, y2, "label")</c>.</summary>
    public string? Label => Arguments.OfType<string>().FirstOrDefault();

    public override string ToString() => $"{Name}({string.Join(", ", Arguments.Select(Describe))})";

    private static string Describe(object? value) => value switch
    {
        null => "null",
        string text => $"\"{text}\"",
        double number => number.ToString(CultureInfo.InvariantCulture),
        object?[] array => $"[{string.Join(", ", array.Select(Describe))}]",
        Dictionary<string, object?> map => $"{{{string.Join(", ", map.Select(pair => $"{pair.Key}: {Describe(pair.Value)}"))}}}",
        bool flag => flag ? "true" : "false",
        _ => value.ToString() ?? ""
    };
}
