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

    /// <summary>
    /// Parses a list whose origins are unknown: any string that looks like an option is one.
    /// </summary>
    /// <remarks>
    /// Prefer <see cref="Parse(CommandContext)"/> for a command's own arguments. This form cannot
    /// tell <c>-r</c> written by the user from a file called <c>-r</c> held in a variable
    /// (<c>TOSH-0013</c>).
    /// </remarks>
    public static ParsedCommandArguments Parse(IReadOnlyList<object?> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return Parse(arguments, static _ => true);
    }

    /// <summary>
    /// Parses a command's arguments, reading an option only from an argument the user wrote as an
    /// unquoted word (<c>TOSH-0013</c>).
    /// </summary>
    /// <remarks>
    /// <c>rm -r dir</c> still recurses; <c>var name = "-r"; rm $name dir</c> removes a file called
    /// <c>-r</c> and refuses the directory. A quoted <c>"-r"</c> is an operand too, which is how
    /// such a file is named on purpose, and <c>--</c> still ends the options.
    /// </remarks>
    public static ParsedCommandArguments Parse(CommandContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Parse(context.Arguments, context.MayBeOption);
    }

    private static ParsedCommandArguments Parse(IReadOnlyList<object?> arguments, Func<int, bool> mayBeOption)
    {
        var positionals = new List<object?>();
        var flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var parseOptions = true;

        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];

            if (!parseOptions || argument is not string text || text.Length == 0 || !mayBeOption(index))
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
