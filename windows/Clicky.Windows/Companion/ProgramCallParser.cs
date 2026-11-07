using System.Globalization;
using System.Text;

namespace Clicky.Windows.Companion;

internal enum ProgramParseOutcome
{
    /// <summary>A whole call was read.</summary>
    Complete,

    /// <summary>The text ends partway through a call; more is still streaming in.</summary>
    Incomplete,

    /// <summary>The text at this point isn't a call.</summary>
    Invalid
}

/// <summary>
/// Reads one call, <c>name(arg, arg, ...)</c> plus an optional <c>;</c>, from a reply program. It only tokenizes: strings
/// (single or double quoted, with \" \' \\ \n \t \uXXXX escapes), numbers, arrays, <c>{key: value}</c> objects, true, false and
/// null. Nothing the model writes is evaluated.
/// </summary>
internal static class ProgramCallParser
{
    public static ProgramParseOutcome TryParse(string text, int start, out ProgramCall? call, out int end)
    {
        call = null;
        end = start;
        int position = start;
        SkipSpaces(text, ref position);
        if (position >= text.Length) return ProgramParseOutcome.Incomplete;
        if (!IsIdentifierStart(text[position])) return ProgramParseOutcome.Invalid;
        int nameStart = position;
        while (position < text.Length && IsIdentifierPart(text[position])) position++;
        if (position >= text.Length) return ProgramParseOutcome.Incomplete;
        string name = text[nameStart..position];
        SkipSpaces(text, ref position);
        if (position >= text.Length) return ProgramParseOutcome.Incomplete;
        if (text[position] != '(') return ProgramParseOutcome.Invalid;
        position++;

        List<object?> arguments = [];
        while (true)
        {
            SkipSpaces(text, ref position);
            if (position >= text.Length) return ProgramParseOutcome.Incomplete;
            if (text[position] == ')') { position++; break; }
            ProgramParseOutcome valueOutcome = TryReadValue(text, ref position, out object? value);
            if (valueOutcome != ProgramParseOutcome.Complete) return valueOutcome;
            arguments.Add(value);
            SkipSpaces(text, ref position);
            if (position >= text.Length) return ProgramParseOutcome.Incomplete;
            if (text[position] == ',') { position++; continue; }
            if (text[position] == ')') { position++; break; }
            return ProgramParseOutcome.Invalid;
        }

        // An optional ";" on the same line; the call is complete either way.
        int afterCall = position;
        while (afterCall < text.Length && text[afterCall] is ' ' or '\t') afterCall++;
        if (afterCall < text.Length && text[afterCall] == ';') position = afterCall + 1;
        call = new ProgramCall(name, arguments);
        end = position;
        return ProgramParseOutcome.Complete;
    }

    private static ProgramParseOutcome TryReadValue(string text, ref int position, out object? value)
    {
        value = null;
        char first = text[position];
        if (first is '"' or '\'') return TryReadString(text, ref position, out value);
        if (first == '-' || char.IsAsciiDigit(first) || first == '.') return TryReadNumber(text, ref position, out value);
        if (first == '[') return TryReadArray(text, ref position, out value);
        if (first == '{') return TryReadObject(text, ref position, out value);
        if (IsIdentifierStart(first))
        {
            int wordStart = position;
            while (position < text.Length && IsIdentifierPart(text[position])) position++;
            if (position >= text.Length) return ProgramParseOutcome.Incomplete;
            string word = text[wordStart..position];
            switch (word)
            {
                case "true": value = true; return ProgramParseOutcome.Complete;
                case "false": value = false; return ProgramParseOutcome.Complete;
                case "null" or "undefined": value = null; return ProgramParseOutcome.Complete;
                default: return ProgramParseOutcome.Invalid;
            }
        }
        return ProgramParseOutcome.Invalid;
    }

    private static ProgramParseOutcome TryReadString(string text, ref int position, out object? value)
    {
        value = null;
        char quote = text[position++];
        StringBuilder builder = new();
        while (position < text.Length)
        {
            char character = text[position++];
            if (character == quote)
            {
                value = builder.ToString();
                return ProgramParseOutcome.Complete;
            }
            if (character != '\\')
            {
                builder.Append(character);
                continue;
            }
            if (position >= text.Length) return ProgramParseOutcome.Incomplete;
            char escaped = text[position++];
            switch (escaped)
            {
                case 'n': builder.Append('\n'); break;
                case 't': builder.Append('\t'); break;
                case 'r': break;
                case 'u':
                    if (position + 4 > text.Length) return ProgramParseOutcome.Incomplete;
                    if (!int.TryParse(text.AsSpan(position, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int codePoint))
                        return ProgramParseOutcome.Invalid;
                    builder.Append((char)codePoint);
                    position += 4;
                    break;
                default: builder.Append(escaped); break; // \" \' \\ and anything else: the character itself.
            }
        }
        return ProgramParseOutcome.Incomplete;
    }

    private static ProgramParseOutcome TryReadNumber(string text, ref int position, out object? value)
    {
        value = null;
        int numberStart = position;
        if (text[position] == '-') position++;
        while (position < text.Length && (char.IsAsciiDigit(text[position]) || text[position] == '.')) position++;
        // A number that runs to the end of the text may still be growing ("12" of "120").
        if (position >= text.Length) return ProgramParseOutcome.Incomplete;
        if (!double.TryParse(text.AsSpan(numberStart, position - numberStart), NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
            return ProgramParseOutcome.Invalid;
        value = number;
        return ProgramParseOutcome.Complete;
    }

    private static ProgramParseOutcome TryReadArray(string text, ref int position, out object? value)
    {
        value = null;
        position++;
        List<object?> items = [];
        while (true)
        {
            SkipSpaces(text, ref position);
            if (position >= text.Length) return ProgramParseOutcome.Incomplete;
            if (text[position] == ']') { position++; break; }
            ProgramParseOutcome itemOutcome = TryReadValue(text, ref position, out object? item);
            if (itemOutcome != ProgramParseOutcome.Complete) return itemOutcome;
            items.Add(item);
            SkipSpaces(text, ref position);
            if (position >= text.Length) return ProgramParseOutcome.Incomplete;
            if (text[position] == ',') { position++; continue; }
            if (text[position] == ']') { position++; break; }
            return ProgramParseOutcome.Invalid;
        }
        value = items.ToArray();
        return ProgramParseOutcome.Complete;
    }

    private static ProgramParseOutcome TryReadObject(string text, ref int position, out object? value)
    {
        value = null;
        position++;
        Dictionary<string, object?> map = new(StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            SkipSpaces(text, ref position);
            if (position >= text.Length) return ProgramParseOutcome.Incomplete;
            if (text[position] == '}') { position++; break; }
            string key;
            if (text[position] is '"' or '\'')
            {
                ProgramParseOutcome keyOutcome = TryReadString(text, ref position, out object? quotedKey);
                if (keyOutcome != ProgramParseOutcome.Complete) return keyOutcome;
                key = (string)quotedKey!;
            }
            else if (IsIdentifierStart(text[position]))
            {
                int keyStart = position;
                while (position < text.Length && IsIdentifierPart(text[position])) position++;
                if (position >= text.Length) return ProgramParseOutcome.Incomplete;
                key = text[keyStart..position];
            }
            else return ProgramParseOutcome.Invalid;
            SkipSpaces(text, ref position);
            if (position >= text.Length) return ProgramParseOutcome.Incomplete;
            if (text[position] != ':') return ProgramParseOutcome.Invalid;
            position++;
            SkipSpaces(text, ref position);
            if (position >= text.Length) return ProgramParseOutcome.Incomplete;
            ProgramParseOutcome valueOutcome = TryReadValue(text, ref position, out object? entry);
            if (valueOutcome != ProgramParseOutcome.Complete) return valueOutcome;
            map[key] = entry;
            SkipSpaces(text, ref position);
            if (position >= text.Length) return ProgramParseOutcome.Incomplete;
            if (text[position] == ',') { position++; continue; }
            if (text[position] == '}') { position++; break; }
            return ProgramParseOutcome.Invalid;
        }
        value = map;
        return ProgramParseOutcome.Complete;
    }

    private static void SkipSpaces(string text, ref int position)
    {
        while (position < text.Length && char.IsWhiteSpace(text[position])) position++;
    }

    internal static bool IsIdentifierStart(char character) => char.IsAsciiLetter(character) || character == '_';
    internal static bool IsIdentifierPart(char character) => char.IsAsciiLetterOrDigit(character) || character == '_';
}
