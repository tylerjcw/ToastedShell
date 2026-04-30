namespace Tosh.Runtime;

public static class CommandDisplaySelectionParser
{
    public static CommandDisplaySelectionParseResult Parse(IReadOnlyList<object?> arguments)
    {
        return Parse(arguments, showOptionAliases: null, hideOptionAliases: null, showAllAliases: null);
    }

    public static CommandDisplaySelectionParseResult Parse(
        IReadOnlyList<object?> arguments,
        IEnumerable<string>? showOptionAliases,
        IEnumerable<string>? hideOptionAliases = null,
        IEnumerable<string>? showAllAliases = null)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var remaining = new List<object?>();
        var showColumns = new List<string>();
        var hideColumns = new List<string>();
        var showAll = false;
        var parseOptions = true;
        var showAliases = BuildAliasSet("--show", showOptionAliases);
        var hideAliases = BuildAliasSet("--hide", hideOptionAliases);
        var showAllSet = BuildAliasSet("--show-all", showAllAliases);

        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];

            if (!parseOptions || argument is not string text || text.Length == 0)
            {
                remaining.Add(argument);
                continue;
            }

            if (text == "--")
            {
                parseOptions = false;
                remaining.Add(argument);
                continue;
            }

            if (MatchesOption(text, showAllSet, out _))
            {
                showAll = true;
                continue;
            }

            if (MatchesOption(text, showAliases, out var showInlineValue))
            {
                AddColumns(showColumns, showInlineValue ?? RequireValue(arguments, ++index, "--show"));
                continue;
            }

            if (MatchesOption(text, hideAliases, out var hideInlineValue))
            {
                AddColumns(hideColumns, hideInlineValue ?? RequireValue(arguments, ++index, "--hide"));
                continue;
            }

            remaining.Add(argument);
        }

        return new CommandDisplaySelectionParseResult(
            new DisplayColumnSelection(showColumns, hideColumns, showAll),
            remaining);
    }

    public static object? Apply(ToshRuntime runtime, DisplayColumnSelection selection, object? value)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(selection);

        if (value is not null && selection.HasOverrides)
        {
            runtime.RegisterDisplaySelection(value, selection);
        }

        return value;
    }

    private static string RequireValue(IReadOnlyList<object?> arguments, int index, string optionName)
    {
        if (index >= arguments.Count)
        {
            throw new InvalidOperationException($"Option '{optionName}' requires a comma-separated property list.");
        }

        return arguments[index]?.ToString() switch
        {
            { Length: > 0 } text => text,
            _ => throw new InvalidOperationException($"Option '{optionName}' requires a comma-separated property list."),
        };
    }

    private static void AddColumns(List<string> target, string specification)
    {
        if (string.IsNullOrWhiteSpace(specification))
        {
            throw new InvalidOperationException("Property selection cannot be empty.");
        }

        foreach (var candidate in specification.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (target.Any(existing => string.Equals(existing, candidate, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            target.Add(candidate);
        }

        if (target.Count == 0)
        {
            throw new InvalidOperationException("Property selection cannot be empty.");
        }
    }

    private static HashSet<string> BuildAliasSet(string primary, IEnumerable<string>? additional)
    {
        var aliases = new HashSet<string>(StringComparer.Ordinal)
        {
            primary,
        };

        if (additional is null)
        {
            return aliases;
        }

        foreach (var alias in additional)
        {
            if (!string.IsNullOrWhiteSpace(alias))
            {
                aliases.Add(alias.Trim());
            }
        }

        return aliases;
    }

    private static bool MatchesOption(string text, IReadOnlyCollection<string> aliases, out string? inlineValue)
    {
        foreach (var alias in aliases)
        {
            if (string.Equals(text, alias, StringComparison.Ordinal))
            {
                inlineValue = null;
                return true;
            }

            if (alias.StartsWith("--", StringComparison.Ordinal) &&
                text.StartsWith(alias + "=", StringComparison.Ordinal))
            {
                inlineValue = text[(alias.Length + 1)..];
                return true;
            }
        }

        inlineValue = null;
        return false;
    }
}
