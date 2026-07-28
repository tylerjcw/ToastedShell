namespace Tosh.Language.Parsing;

/// <summary>
/// What the parser knows about the world outside the text it is parsing
/// (TS-P2-23).
///
/// Parse-time identity decisions used to rest on spelling — a dotted name
/// beginning with a capital was assumed to be a CLR type — because
/// <see cref="ToshParser.Parse(string, string, ParseContext?)"/> received
/// only source text while the registries arrived later at
/// <c>Lowerer.Lower</c>. Supplying them here lets those decisions consult
/// a table instead.
///
/// <see cref="Empty"/> is a legitimate value, not a compatibility shim:
/// the formatter, the REPL continuation classifier, and interpolation-hole
/// parsing all parse text with no environment and must keep working.
/// Names absent from the context simply fall through to the ordinary
/// bareword reading, and the engine reports an unresolved name at run
/// time with a proper diagnostic.
/// </summary>
public sealed class ParseContext
{
    /// <summary>
    /// A context that knows nothing. Parsing stays purely syntactic.
    /// </summary>
    public static readonly ParseContext Empty = new(null, null, null);

    private readonly IReadOnlySet<string>? _commandNames;
    private readonly IReadOnlySet<string>? _moduleNames;
    private readonly IReadOnlySet<string>? _typeNames;

    private ParseContext(
        IReadOnlySet<string>? commandNames,
        IReadOnlySet<string>? moduleNames,
        IReadOnlySet<string>? typeNames)
    {
        _commandNames = commandNames;
        _moduleNames = moduleNames;
        _typeNames = typeNames;
    }

    /// <summary>
    /// Builds a context from names the host already has. Each set is
    /// optional so callers can supply only what they know.
    /// </summary>
    public static ParseContext Create(
        IEnumerable<string>? commandNames = null,
        IEnumerable<string>? moduleNames = null,
        IEnumerable<string>? typeNames = null)
    {
        static IReadOnlySet<string>? ToSet(IEnumerable<string>? names)
        {
            if (names is null)
            {
                return null;
            }

            var set = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
            return set.Count == 0 ? null : set;
        }

        var commands = ToSet(commandNames);
        var modules = ToSet(moduleNames);
        var types = ToSet(typeNames);

        return commands is null && modules is null && types is null
            ? Empty
            : new ParseContext(commands, modules, types);
    }

    /// <summary>True when the host has a command registered under this name.</summary>
    public bool IsKnownCommand(string? name) =>
        name is { Length: > 0 } && _commandNames?.Contains(name) == true;

    /// <summary>
    /// True when <paramref name="name"/>'s leading dotted segment names a
    /// module the host knows about, which is how an *imported* module is
    /// recognised — the source being parsed never declares it.
    /// </summary>
    public bool IsKnownModuleQualifier(string? name)
    {
        if (name is not { Length: > 0 } || _moduleNames is null)
        {
            return false;
        }

        var separator = name.IndexOf('.');
        var qualifier = separator < 0 ? name : name[..separator];
        return qualifier.Length > 0 && _moduleNames.Contains(qualifier);
    }

    /// <summary>True when the host knows a type by this name.</summary>
    public bool IsKnownType(string? name) =>
        name is { Length: > 0 } && _typeNames?.Contains(name) == true;
}
