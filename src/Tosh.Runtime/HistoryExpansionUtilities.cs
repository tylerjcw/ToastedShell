namespace Tosh.Runtime;

internal static class HistoryExpansionUtilities
{
    public static CommandHistoryEntry ResolveEntry(IReadOnlyList<CommandHistoryEntry> history, string spec, bool allowPrefixSearch = true)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentException.ThrowIfNullOrWhiteSpace(spec);

        var normalized = spec.Trim();

        if (normalized.StartsWith('!'))
        {
            normalized = normalized[1..];
        }

        if (normalized.Length == 0)
        {
            throw new InvalidOperationException("History expansion needs a non-empty designator.");
        }

        if (normalized == "!")
        {
            return ResolveLast(history);
        }

        if (normalized[0] == '-')
        {
            if (!int.TryParse(normalized[1..], out var offset) || offset <= 0)
            {
                throw new InvalidOperationException($"History designator '!{normalized}' is not valid.");
            }

            return ResolveRelative(history, offset);
        }

        if (normalized[0] == '?')
        {
            if (normalized.Length < 3 || normalized[^1] != '?')
            {
                throw new InvalidOperationException($"History search designator '!{normalized}' must end with '?'.");
            }

            var needle = normalized[1..^1];

            if (needle.Length == 0)
            {
                throw new InvalidOperationException("History search designator cannot be empty.");
            }

            return ResolveContains(history, needle);
        }

        if (long.TryParse(normalized, out var id))
        {
            return ResolveById(history, id);
        }

        if (allowPrefixSearch)
        {
            return ResolvePrefix(history, normalized);
        }

        throw new InvalidOperationException($"History designator '!{normalized}' is not supported here.");
    }

    public static string Expand(IReadOnlyList<CommandHistoryEntry> history, string spec, bool allowPrefixSearch = true)
    {
        var expansionSpec = ParseExpansionSpec(spec);
        var entry = ResolveEntry(history, expansionSpec.EventSpec, allowPrefixSearch);
        return ApplyWordDesignator(entry.Text, expansionSpec.WordDesignator);
    }

    private static HistoryExpansionSpec ParseExpansionSpec(string spec)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(spec);

        var normalized = spec.Trim();

        if (normalized is "!^" or "!$" or "!*" or "^" or "$" or "*")
        {
            return new HistoryExpansionSpec("!!", normalized[^1] switch
            {
                '^' => HistoryWordDesignator.FirstArgument,
                '$' => HistoryWordDesignator.LastWord,
                '*' => HistoryWordDesignator.AllArguments,
                _ => HistoryWordDesignator.None,
            });
        }

        if (normalized.Length >= 2 &&
            normalized[^2] == ':' &&
            TryParseWordDesignator(normalized[^1], out var wordDesignator))
        {
            return new HistoryExpansionSpec(normalized[..^2], wordDesignator);
        }

        return new HistoryExpansionSpec(normalized, HistoryWordDesignator.None);
    }

    private static string ApplyWordDesignator(string commandText, HistoryWordDesignator wordDesignator)
    {
        if (wordDesignator == HistoryWordDesignator.None)
        {
            return commandText;
        }

        var words = SplitHistoryWords(commandText);

        if (words.Count == 0)
        {
            throw new InvalidOperationException("History entry does not contain any words to expand.");
        }

        return wordDesignator switch
        {
            HistoryWordDesignator.FirstArgument when words.Count >= 2 => words[1],
            HistoryWordDesignator.FirstArgument => throw new InvalidOperationException("History entry does not have a first argument."),
            HistoryWordDesignator.LastWord => words[^1],
            HistoryWordDesignator.AllArguments => words.Count >= 2 ? string.Join(" ", words.Skip(1)) : string.Empty,
            _ => commandText,
        };
    }

    private static IReadOnlyList<string> SplitHistoryWords(string commandText)
    {
        var normalized = commandText
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        var words = new List<string>();
        var wordStart = -1;
        var inSingleQuote = false;
        var inDoubleQuote = false;
        var escaping = false;

        for (var index = 0; index < normalized.Length; index++)
        {
            var current = normalized[index];

            if (wordStart < 0)
            {
                if (char.IsWhiteSpace(current))
                {
                    continue;
                }

                if (current == '#')
                {
                    break;
                }

                wordStart = index;
            }

            if (escaping)
            {
                escaping = false;
                continue;
            }

            if (!inSingleQuote && current == '\\')
            {
                escaping = true;
                continue;
            }

            if (!inDoubleQuote && current == '\'')
            {
                inSingleQuote = !inSingleQuote;
                continue;
            }

            if (!inSingleQuote && current == '"')
            {
                inDoubleQuote = !inDoubleQuote;
                continue;
            }

            if (!inSingleQuote && !inDoubleQuote && char.IsWhiteSpace(current))
            {
                words.Add(normalized[wordStart..index]);
                wordStart = -1;
            }
        }

        if (wordStart >= 0)
        {
            words.Add(normalized[wordStart..]);
        }

        return words;
    }

    private static bool TryParseWordDesignator(char character, out HistoryWordDesignator wordDesignator)
    {
        wordDesignator = character switch
        {
            '^' => HistoryWordDesignator.FirstArgument,
            '$' => HistoryWordDesignator.LastWord,
            '*' => HistoryWordDesignator.AllArguments,
            _ => HistoryWordDesignator.None,
        };

        return wordDesignator != HistoryWordDesignator.None;
    }

    private static CommandHistoryEntry ResolveLast(IReadOnlyList<CommandHistoryEntry> history)
    {
        if (history.Count == 0)
        {
            throw new InvalidOperationException("History is empty.");
        }

        return history[^1];
    }

    private static CommandHistoryEntry ResolveRelative(IReadOnlyList<CommandHistoryEntry> history, int offset)
    {
        if (history.Count == 0 || offset > history.Count)
        {
            throw new InvalidOperationException($"History does not go back {offset} entr{(offset == 1 ? "y" : "ies")}.");
        }

        return history[^offset];
    }

    private static CommandHistoryEntry ResolveById(IReadOnlyList<CommandHistoryEntry> history, long id)
    {
        var match = history.LastOrDefault(entry => entry.Id == id);

        return match ?? throw new InvalidOperationException($"History entry '{id}' was not found.");
    }

    private static CommandHistoryEntry ResolveContains(IReadOnlyList<CommandHistoryEntry> history, string text)
    {
        for (var index = history.Count - 1; index >= 0; index--)
        {
            if (history[index].Text.Contains(text, StringComparison.OrdinalIgnoreCase))
            {
                return history[index];
            }
        }

        throw new InvalidOperationException($"No history entry contains '{text}'.");
    }

    private static CommandHistoryEntry ResolvePrefix(IReadOnlyList<CommandHistoryEntry> history, string prefix)
    {
        for (var index = history.Count - 1; index >= 0; index--)
        {
            if (history[index].Text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return history[index];
            }
        }

        throw new InvalidOperationException($"No history entry starts with '{prefix}'.");
    }

    private readonly record struct HistoryExpansionSpec(string EventSpec, HistoryWordDesignator WordDesignator);

    private enum HistoryWordDesignator
    {
        None,
        FirstArgument,
        LastWord,
        AllArguments,
    }
}
