using System.Diagnostics;
using System.IO;

namespace Clicky.Windows.Companion;

/// <summary>
/// Facts the model chose to remember about the user (E11 T11.24): one per line in memory.md next to clicky.config.json, so
/// the user can read and edit them. Every turn's context carries them as a [memory] line, capped at about 2k tokens (the
/// newest facts win). <c>remember("…")</c> adds one (exact repeats are skipped); <c>forget("…")</c> removes every fact
/// containing those words.
/// </summary>
internal sealed class CompanionMemory(string memoryFilePath)
{
    internal const int MaximumContextCharacters = 8000;
    private const int MaximumFactCharacters = 300;
    private readonly object fileGate = new();

    public string MemoryFilePath => memoryFilePath;

    /// <summary>memory.md in the same folder as the config file, so a test run's copy keeps its own memory too.</summary>
    public static string PathNextTo(string configurationFilePath) =>
        Path.Combine(Path.GetDirectoryName(configurationFilePath) ?? ".", "memory.md");

    public IReadOnlyList<string> Facts
    {
        get
        {
            lock (fileGate)
            {
                try
                {
                    return File.Exists(memoryFilePath)
                        ? File.ReadAllLines(memoryFilePath).Select(line => line.Trim()).Where(line => line.StartsWith("- "))
                            .Select(line => line[2..].Trim()).Where(fact => fact.Length > 0).ToArray()
                        : [];
                }
                catch (IOException exception)
                {
                    Trace.TraceWarning($"Couldn't read the memory file: {exception.Message}");
                    return [];
                }
            }
        }
    }

    public void Remember(string fact)
    {
        fact = fact.ReplaceLineEndings(" ").Trim();
        if (fact.Length == 0) return;
        if (fact.Length > MaximumFactCharacters) fact = fact[..MaximumFactCharacters];
        List<string> facts = [.. Facts];
        if (facts.Any(existing => existing.Equals(fact, StringComparison.OrdinalIgnoreCase))) return;
        facts.Add(fact);
        Save(facts);
        Trace.WriteLine($"Remembered: {fact}");
    }

    public void Forget(string words)
    {
        words = words.Trim();
        if (words.Length == 0) return;
        List<string> facts = [.. Facts];
        int removed = facts.RemoveAll(fact => fact.Contains(words, StringComparison.OrdinalIgnoreCase));
        if (removed == 0) return;
        Save(facts);
        Trace.WriteLine($"Forgot {removed} fact(s) containing \"{words}\".");
    }

    /// <summary>The [memory] context line, newest facts first to survive the cap; null with nothing remembered.</summary>
    public string? ContextLine()
    {
        IReadOnlyList<string> facts = Facts;
        if (facts.Count == 0) return null;
        List<string> kept = [];
        int length = 0;
        foreach (string fact in facts.Reverse())
        {
            if (length + fact.Length + 2 > MaximumContextCharacters) break;
            kept.Insert(0, fact);
            length += fact.Length + 2;
        }
        return "[memory] " + string.Join("; ", kept);
    }

    private void Save(IEnumerable<string> facts)
    {
        lock (fileGate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(memoryFilePath)!);
                File.WriteAllLines(memoryFilePath,
                    ["# What Clicky remembers about you. One fact per line; edit or delete freely.", .. facts.Select(fact => "- " + fact)]);
            }
            catch (IOException exception)
            {
                Trace.TraceWarning($"Couldn't save the memory file: {exception.Message}");
            }
        }
    }
}
