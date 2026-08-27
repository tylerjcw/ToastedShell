namespace Tosh.Runtime;

public sealed class ParsedCommandArguments
{
    private readonly HashSet<string> _flags;

    private ParsedCommandArguments(IReadOnlyList<object?> positionals, HashSet<string> flags)
    {
        Positionals = positionals;
        _flags = flags;
    }

    public IReadOnlyList<object?> Positionals { get; }

    public IReadOnlyCollection<string> Flags => _flags;

    public static ParsedCommandArguments Parse(IReadOnlyList<object?> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var positionals = new List<object?>();
        var flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var parseOptions = true;

        foreach (var argument in arguments)
        {
            if (!parseOptions || argument is not string text || text.Length == 0)
            {
                positionals.Add(argument);
                continue;
            }

            if (text == "--")
            {
                parseOptions = false;
                continue;
            }

            if (text.StartsWith("--", StringComparison.Ordinal) && text.Length > 2)
            {
                flags.Add(text[2..]);
                continue;
            }

            if (text.StartsWith("-", StringComparison.Ordinal) && text.Length > 1)
            {
                foreach (var flag in text[1..])
                {
                    flags.Add(flag.ToString());
                }

                continue;
            }

            positionals.Add(argument);
        }

        return new ParsedCommandArguments(positionals, flags);
    }

    public bool HasFlag(params string[] names)
    {
        ArgumentNullException.ThrowIfNull(names);
        return names.Any(name => _flags.Contains(name));
    }
}
