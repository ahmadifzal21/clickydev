using System.Diagnostics;
using System.Globalization;

namespace Clicky.Windows.Companion;

/// <summary>
/// Times one turn from its start (the key release, or the typed question) and logs the stages as one line when the turn ends,
/// e.g. "Turn timing from release: transcript 0.92 s, capture 1.20 s, answer 9.80 s, first sound 11.40 s, done 15.10 s".
/// Only the first mark of each stage counts. Marks can come from any thread.
/// </summary>
internal sealed class TurnTimeline(string startedAt)
{
    private readonly Stopwatch sinceStart = Stopwatch.StartNew();
    private readonly List<(string Stage, TimeSpan Elapsed)> marks = [];

    public void Mark(string stage)
    {
        lock (marks)
        {
            if (marks.All(mark => mark.Stage != stage)) marks.Add((stage, sinceStart.Elapsed));
        }
    }

    public string Describe()
    {
        lock (marks)
        {
            return $"Turn timing from {startedAt}: " + string.Join(", ",
                marks.Select(mark => string.Create(CultureInfo.InvariantCulture, $"{mark.Stage} {mark.Elapsed.TotalSeconds:0.00} s")));
        }
    }
}
