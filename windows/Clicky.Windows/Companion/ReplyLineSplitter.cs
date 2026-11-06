using System.Text;
using System.Text.RegularExpressions;

namespace Clicky.Windows.Companion;

/// <summary>A piece of the reply ready to speak; <paramref name="StartsLine"/> is false for a later sentence of the same line.</summary>
internal sealed record ReplyLine(string Text, bool StartsLine);

/// <summary>
/// Turns a reply that arrives in pieces into lines that can be spoken as soon as each one is complete: a line ends at a
/// newline, or at a sentence end (". ", "! ", "? ") inside a long line. A [POINT…] tag stays on the line it ends, so it is never
/// spoken on its own and is parsed with that line (PointTagParser). Not thread-safe: one turn feeds it from one thread.
/// </summary>
internal sealed partial class ReplyLineSplitter
{
    private readonly StringBuilder pendingText = new();
    private readonly StringBuilder allText = new();
    private bool isAtLineStart = true;

    /// <summary>Adds the next piece and returns the lines it completed (often none).</summary>
    public IReadOnlyList<ReplyLine> Add(string textDelta)
    {
        pendingText.Append(textDelta);
        allText.Append(textDelta);
        return TakeLines(isReplyComplete: false);
    }

    /// <summary>The reply is complete: returns whatever is left as the last line(s).</summary>
    public IReadOnlyList<ReplyLine> Finish() => TakeLines(isReplyComplete: true);

    /// <summary>The reply so far as the bubble should show it: complete [POINT…] tags removed, an unfinished one hidden.</summary>
    public string VisibleText
    {
        get
        {
            string text = PointTagRegex().Replace(allText.ToString(), "");
            int unfinishedTagStart = text.LastIndexOf('[');
            if (unfinishedTagStart >= 0 && text.IndexOf(']', unfinishedTagStart) < 0) text = text[..unfinishedTagStart];
            return string.Join("\n", text.Split('\n').Select(line => MultipleSpacesRegex().Replace(line, " ").Trim())).Trim();
        }
    }

    private List<ReplyLine> TakeLines(bool isReplyComplete)
    {
        List<ReplyLine> lines = [];
        while (FindLineEnd(isReplyComplete) is { } lineEnd)
        {
            string line = pendingText.ToString(0, lineEnd.Length).Trim();
            pendingText.Remove(0, lineEnd.Length);
            if (line.Length > 0)
            {
                lines.Add(new ReplyLine(line, isAtLineStart));
                isAtLineStart = false;
            }
            if (lineEnd.EndsLine) isAtLineStart = true;
        }
        return lines;
    }

    /// <summary>The next complete piece in <see cref="pendingText"/> (and whether it ends its line), or null if none is complete.</summary>
    private (int Length, bool EndsLine)? FindLineEnd(bool isReplyComplete)
    {
        string text = pendingText.ToString();
        if (text.Length == 0) return null;
        int newline = text.IndexOf('\n');
        string firstLine = newline >= 0 ? text[..newline] : text;
        for (int index = 0; index + 1 < firstLine.Length; index++)
        {
            if (firstLine[index] is not ('.' or '!' or '?') || !char.IsWhiteSpace(firstLine[index + 1])) continue;
            // Only split where more words follow; a tag after the sentence belongs to it, and so does an unseen next character.
            int nextVisible = index + 1;
            while (nextVisible < firstLine.Length && char.IsWhiteSpace(firstLine[nextVisible])) nextVisible++;
            if (nextVisible < firstLine.Length && firstLine[nextVisible] != '[') return (index + 1, false);
        }
        if (newline >= 0) return (newline + 1, true);
        return isReplyComplete ? (text.Length, true) : null;
    }

    [GeneratedRegex(@"\[POINT:[^\]]*\]")]
    private static partial Regex PointTagRegex();

    [GeneratedRegex(@"[ \t]{2,}")]
    private static partial Regex MultipleSpacesRegex();
}
