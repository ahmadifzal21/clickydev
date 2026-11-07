using System.Diagnostics;
using System.Text;

namespace Clicky.Windows.Companion;

/// <summary>
/// Turns a reply that streams in as text into program calls as soon as each one is complete (E11 T11.4). The model writes a
/// fenced block (```clicky, or any fence) with one call per line: <c>say("…")</c>, marks such as <c>ring(…)</c>, actions and
/// <c>seen("…")</c>. A bare call line outside a fence counts too. Anything else is prose: it is split into spoken lines as
/// before (ReplyLineSplitter) and old [POINT] tags become <c>point</c> calls, so a model that ignores the format still works.
/// <c>screen(n)</c> is applied here: later calls carry that screen number. Not thread-safe: one turn feeds it from one thread.
/// </summary>
internal sealed class ReplyProgramReader
{
    /// <summary>Calls the app understands; a bare line outside a fence must start with one of these to be read as code.</summary>
    public static readonly HashSet<string> KnownCallNames = new(StringComparer.Ordinal)
    {
        "step", "say", "seen", "screen", "clear",
        "point", "ring", "box", "underline", "highlight", "arrow", "trace", "label", "card", "scrollhint", "click", "hover",
        "remember", "forget", "copy", "result", "open", "agent", "agent_send", "agent_stop", "figure3d"
    };

    private readonly StringBuilder pendingText = new();
    private readonly ReplyLineSplitter proseSplitter = new();
    private readonly StringBuilder visibleText = new();
    private bool isInFence;
    private bool isInBareCodeLine;
    private bool isAtLineStart = true;
    private int? currentScreenNumber;

    /// <summary>The spoken lines so far: sentences of one line joined by a space, lines by a newline.</summary>
    public string VisibleText => visibleText.ToString();

    public IReadOnlyList<ProgramCall> Add(string textDelta)
    {
        pendingText.Append(textDelta);
        return Read(isReplyComplete: false);
    }

    /// <summary>The reply is complete: whatever is left is read (an unfinished call at the very end is dropped).</summary>
    public IReadOnlyList<ProgramCall> Finish() => Read(isReplyComplete: true);

    /// <summary>A whole reply at once, e.g. for the panel or for history.</summary>
    public static (IReadOnlyList<ProgramCall> Calls, string VisibleText) ReadAll(string replyText)
    {
        ReplyProgramReader reader = new();
        List<ProgramCall> calls = [.. reader.Add(replyText), .. reader.Finish()];
        return (calls, reader.VisibleText);
    }

    private List<ProgramCall> Read(bool isReplyComplete)
    {
        List<ProgramCall> calls = [];
        while (pendingText.Length > 0)
        {
            string text = pendingText.ToString();
            bool keepGoing = isInFence || isInBareCodeLine
                ? ReadCode(text, isReplyComplete, calls)
                : ReadOutsideCode(text, isReplyComplete, calls);
            if (!keepGoing) break;
        }
        if (isReplyComplete)
        {
            foreach (ReplyLine proseLine in proseSplitter.Finish()) EmitProse(proseLine, calls);
            pendingText.Clear();
        }
        return calls;
    }

    /// <summary>Reads calls from the start of <paramref name="text"/>; false when it must wait for more text.</summary>
    private bool ReadCode(string text, bool isReplyComplete, List<ProgramCall> calls)
    {
        int position = 0;
        while (position < text.Length && (text[position] is ' ' or '\t' or '\r' or ';' || (text[position] == '\n' && !isInBareCodeLine)))
            position++;
        if (position >= text.Length)
        {
            Consume(position);
            return false;
        }
        if (text[position] == '\n') // The end of a bare code line: back to prose.
        {
            Consume(position + 1);
            isInBareCodeLine = false;
            isAtLineStart = true;
            return true;
        }
        if (isInFence && text[position] == '`')
        {
            if (text.Length - position < 3 && !isReplyComplete)
            {
                Consume(position);
                return false;
            }
            if (text.AsSpan(position).StartsWith("```"))
            {
                int fenceLineEnd = text.IndexOf('\n', position);
                if (fenceLineEnd < 0 && !isReplyComplete)
                {
                    Consume(position);
                    return false;
                }
                Consume(fenceLineEnd < 0 ? text.Length : fenceLineEnd + 1);
                isInFence = false;
                isAtLineStart = true;
                return true;
            }
        }
        if (text.AsSpan(position).StartsWith("//") || text[position] == '#')
            return SkipRestOfLine(text, position, isReplyComplete);

        switch (ProgramCallParser.TryParse(text, position, out ProgramCall? call, out int end))
        {
            case ProgramParseOutcome.Complete:
                Consume(end);
                Emit(call!, calls);
                return true;
            case ProgramParseOutcome.Incomplete:
                if (!isReplyComplete)
                {
                    Consume(position);
                    return false;
                }
                Trace.TraceWarning($"Reply program ended inside a call: {text[position..].Trim()}");
                Consume(text.Length);
                return false;
            default:
                Trace.TraceWarning($"Reply program line isn't a call, skipped: {FirstLine(text, position)}");
                return SkipRestOfLine(text, position, isReplyComplete);
        }
    }

    private bool ReadOutsideCode(string text, bool isReplyComplete, List<ProgramCall> calls)
    {
        if (isAtLineStart)
        {
            int position = 0;
            while (position < text.Length && text[position] is ' ' or '\t') position++;
            if (position < text.Length && text[position] == '`')
            {
                if (text.Length - position < 3 && !isReplyComplete) return false;
                if (text.AsSpan(position).StartsWith("```"))
                {
                    int fenceLineEnd = text.IndexOf('\n', position);
                    if (fenceLineEnd < 0 && !isReplyComplete) return false;
                    Consume(fenceLineEnd < 0 ? text.Length : fenceLineEnd + 1);
                    isInFence = true;
                    return true;
                }
            }
            if (position < text.Length && ProgramCallParser.IsIdentifierStart(text[position]))
            {
                int wordEnd = position;
                while (wordEnd < text.Length && ProgramCallParser.IsIdentifierPart(text[wordEnd])) wordEnd++;
                int afterWord = wordEnd;
                while (afterWord < text.Length && text[afterWord] is ' ' or '\t') afterWord++;
                if (afterWord >= text.Length && !isReplyComplete) return false; // Can't tell prose from code yet.
                if (afterWord < text.Length && text[afterWord] == '(' && KnownCallNames.Contains(text[position..wordEnd]))
                {
                    isInBareCodeLine = true;
                    return true;
                }
            }
            else if (position >= text.Length && !isReplyComplete) return false;
        }

        int newline = text.IndexOf('\n');
        string piece = newline < 0 ? text : text[..(newline + 1)];
        Consume(piece.Length);
        isAtLineStart = newline >= 0;
        foreach (ReplyLine proseLine in proseSplitter.Add(piece)) EmitProse(proseLine, calls);
        return true;
    }

    private bool SkipRestOfLine(string text, int position, bool isReplyComplete)
    {
        int lineEnd = text.IndexOf('\n', position);
        if (lineEnd < 0)
        {
            if (!isReplyComplete)
            {
                Consume(position);
                return false;
            }
            Consume(text.Length);
            return false;
        }
        Consume(isInBareCodeLine ? lineEnd : lineEnd + 1);
        return true;
    }

    private void EmitProse(ReplyLine proseLine, List<ProgramCall> calls)
    {
        (string spokenText, IReadOnlyList<PointingParseResult> tags) = PointTagParser.ExtractTags(proseLine.Text);
        if (spokenText.Length > 0) Emit(new ProgramCall("say", [spokenText]) { ContinuesLine = !proseLine.StartsLine }, calls);
        foreach (PointingParseResult tag in tags)
        {
            ScreenshotPixelCoordinate coordinate = tag.ScreenshotCoordinate!.Value;
            calls.Add(new ProgramCall("point", [coordinate.X, coordinate.Y, tag.ElementLabel], tag.ScreenNumber));
        }
    }

    private void Emit(ProgramCall call, List<ProgramCall> calls)
    {
        if (call.Name == "screen")
        {
            currentScreenNumber = call.Number(0) is { } screenNumber && screenNumber >= 1 ? (int)screenNumber : null;
            return;
        }
        if (call.Name == "say")
        {
            string? spokenText = call.Text(0)?.Trim();
            if (string.IsNullOrEmpty(spokenText)) return;
            if (visibleText.Length > 0) visibleText.Append(call.ContinuesLine ? ' ' : '\n');
            visibleText.Append(spokenText);
            call = call with { Arguments = [spokenText] };
        }
        calls.Add(call.ScreenNumber is null && currentScreenNumber is not null ? call with { ScreenNumber = currentScreenNumber } : call);
    }

    private void Consume(int length) => pendingText.Remove(0, length);

    private static string FirstLine(string text, int position)
    {
        int lineEnd = text.IndexOf('\n', position);
        return (lineEnd < 0 ? text[position..] : text[position..lineEnd]).Trim();
    }
}
