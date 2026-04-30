using System.Text;
using Tosh.Runtime;

namespace Tosh.Cli;

internal static class ReplHistoryExpander
{
    public static HistoryExpansionResult Expand(string source, IReadOnlyList<CommandHistoryEntry> history)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(history);

        if (source.Length == 0)
        {
            return new HistoryExpansionResult(source, false);
        }

        if (TryExpandQuickSubstitution(source, history, out var substituted))
        {
            return new HistoryExpansionResult(substituted, true);
        }

        var builder = new StringBuilder(source.Length);
        var expanded = false;
        var inSingleQuote = false;
        var inDoubleQuote = false;
        var escaping = false;
        var inComment = false;

        for (var index = 0; index < source.Length; index++)
        {
            var current = source[index];

            if (inComment)
            {
                builder.Append(current);

                if (current == '\n')
                {
                    inComment = false;
                }

                continue;
            }

            if (escaping)
            {
                builder.Append(current);
                escaping = false;
                continue;
            }

            if (current == '\\')
            {
                builder.Append(current);
                escaping = true;
                continue;
            }

            if (!inDoubleQuote && current == '\'')
            {
                inSingleQuote = !inSingleQuote;
                builder.Append(current);
                continue;
            }

            if (!inSingleQuote && current == '"')
            {
                inDoubleQuote = !inDoubleQuote;
                builder.Append(current);
                continue;
            }

            if (!inSingleQuote && !inDoubleQuote && current == '#')
            {
                inComment = true;
                builder.Append(current);
                continue;
            }

            if (!inSingleQuote &&
                !inDoubleQuote &&
                current == '!' &&
                IsHistoryBoundary(source, index) &&
                TryReadDesignator(source, index, out var designator, out var consumedLength))
            {
                builder.Append(HistoryExpansionUtilities.Expand(history, designator));
                expanded = true;
                index += consumedLength - 1;
                continue;
            }

            builder.Append(current);
        }

        return new HistoryExpansionResult(builder.ToString(), expanded);
    }

    private static bool TryExpandQuickSubstitution(string source, IReadOnlyList<CommandHistoryEntry> history, out string result)
    {
        result = string.Empty;

        if (source.Length < 4 || source[0] != '^' || source.Count(character => character == '^') < 3)
        {
            return false;
        }

        var secondCaret = source.IndexOf('^', 1);

        if (secondCaret <= 1)
        {
            return false;
        }

        var thirdCaret = source.IndexOf('^', secondCaret + 1);

        if (thirdCaret <= secondCaret + 1 || thirdCaret != source.Length - 1)
        {
            return false;
        }

        var previous = HistoryExpansionUtilities.Expand(history, "!!");
        var oldText = source[1..secondCaret];
        var newText = source[(secondCaret + 1)..thirdCaret];
        var replaced = previous.Replace(oldText, newText, StringComparison.Ordinal);

        if (string.Equals(replaced, previous, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"History substitution could not find '{oldText}' in the previous command.");
        }

        result = replaced;
        return true;
    }

    private static bool TryReadDesignator(string source, int startIndex, out string designator, out int consumedLength)
    {
        designator = string.Empty;
        consumedLength = 0;

        if (startIndex < 0 || startIndex >= source.Length || source[startIndex] != '!')
        {
            return false;
        }

        if (startIndex + 1 >= source.Length)
        {
            return false;
        }

        var next = source[startIndex + 1];

        if (next == '!')
        {
            consumedLength = 2;
            consumedLength += ReadWordDesignatorSuffixLength(source, startIndex + consumedLength);
            designator = source[startIndex..(startIndex + consumedLength)];
            return true;
        }

        if (next is '^' or '$' or '*')
        {
            designator = source[startIndex..(startIndex + 2)];
            consumedLength = 2;
            return true;
        }

        if (next == '?')
        {
            var end = source.IndexOf('?', startIndex + 2);

            if (end <= startIndex + 2)
            {
                return false;
            }

            consumedLength = (end - startIndex) + 1;
            consumedLength += ReadWordDesignatorSuffixLength(source, startIndex + consumedLength);
            designator = source[startIndex..(startIndex + consumedLength)];
            return true;
        }

        if (char.IsDigit(next))
        {
            var end = startIndex + 2;

            while (end < source.Length && char.IsDigit(source[end]))
            {
                end++;
            }

            consumedLength = end - startIndex;
            consumedLength += ReadWordDesignatorSuffixLength(source, startIndex + consumedLength);
            designator = source[startIndex..(startIndex + consumedLength)];
            return true;
        }

        if (next == '-' && startIndex + 2 < source.Length && char.IsDigit(source[startIndex + 2]))
        {
            var end = startIndex + 3;

            while (end < source.Length && char.IsDigit(source[end]))
            {
                end++;
            }

            consumedLength = end - startIndex;
            consumedLength += ReadWordDesignatorSuffixLength(source, startIndex + consumedLength);
            designator = source[startIndex..(startIndex + consumedLength)];
            return true;
        }

        if (char.IsLetter(next) || next == '_')
        {
            var end = startIndex + 2;

            while (end < source.Length && IsPrefixCharacter(source[end]))
            {
                end++;
            }

            consumedLength = end - startIndex;
            consumedLength += ReadWordDesignatorSuffixLength(source, startIndex + consumedLength);
            designator = source[startIndex..(startIndex + consumedLength)];
            return true;
        }

        return false;
    }

    private static int ReadWordDesignatorSuffixLength(string source, int suffixStart)
    {
        if (suffixStart + 1 >= source.Length || source[suffixStart] != ':')
        {
            return 0;
        }

        return source[suffixStart + 1] is '^' or '$' or '*'
            ? 2
            : 0;
    }

    private static bool IsHistoryBoundary(string source, int index)
    {
        if (index <= 0)
        {
            return true;
        }

        return source[index - 1] switch
        {
            ' ' or '\t' or '\n' or '\r' or '(' or ')' or '{' or '}' or '[' or ']' or '|' or '&' or ';' or '=' or ':' or ','
                => true,
            _ => false,
        };
    }

    private static bool IsPrefixCharacter(char character)
    {
        return char.IsLetterOrDigit(character) || character is '_' or '-' or '.' or '/';
    }
}

internal sealed record HistoryExpansionResult(string Text, bool Expanded);
