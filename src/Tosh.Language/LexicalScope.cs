using Tosh.Runtime;

namespace Tosh.Language;

public sealed class LexicalScope
{
    public LexicalScope(
        IReadOnlyDictionary<string, object?>? variables = null,
        bool isModuleScope = false,
        bool exportDeclarationsByDefault = false)
        : this(variables, isModuleScope, exportDeclarationsByDefault, exports: null)
    {
    }

    internal LexicalScope(
        IReadOnlyDictionary<string, object?>? variables,
        bool isModuleScope,
        bool exportDeclarationsByDefault,
        ModuleExportTable? exports)
    {
        Variables = variables is null
            ? new Dictionary<string, object?>(StringComparer.Ordinal)
            : new Dictionary<string, object?>(variables, StringComparer.Ordinal);
        IsModuleScope = isModuleScope;
        ExportDeclarationsByDefault = exportDeclarationsByDefault;
        Exports = isModuleScope ? (exports ?? new ModuleExportTable()) : null;
    }

    public Dictionary<string, object?> Variables { get; }

    /// <summary>
    /// The declaration tables, created when something is first put in one.
    /// </summary>
    /// <remarks>
    /// A scope used to allocate ten collections eagerly, and a scope is pushed for
    /// every block — so every loop iteration built eight dictionaries and two hash
    /// sets that a loop body almost never uses. Measured at 2,797 bytes for an
    /// *empty* `for` iteration, of which this was the largest single share.
    ///
    /// Allocating on first access rather than on construction keeps every call site
    /// unchanged: `scope.Commands[name] = command` still reads the same. The cost is
    /// that a *read* of an empty table allocates it too — acceptable because the
    /// tables that are read on the hot path (variables) are the ones that were always
    /// going to exist.
    /// </remarks>
    public Dictionary<string, IShellCommand> Commands =>
        _commands ??= new Dictionary<string, IShellCommand>(StringComparer.Ordinal);

    public Dictionary<string, object?> Classes =>
        _classes ??= new Dictionary<string, object?>(StringComparer.Ordinal);

    public Dictionary<string, object?> Modules =>
        _modules ??= new Dictionary<string, object?>(StringComparer.Ordinal);

    internal Dictionary<string, RefinementTypeDefinition> RefinementTypes =>
        _refinementTypes ??= new Dictionary<string, RefinementTypeDefinition>(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> TypeImports =>
        _typeImports ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, string> TypeAliases =>
        _typeAliases ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    private Dictionary<string, IShellCommand>? _commands;
    private Dictionary<string, object?>? _classes;
    private Dictionary<string, object?>? _modules;
    private Dictionary<string, RefinementTypeDefinition>? _refinementTypes;
    private HashSet<string>? _typeImports;
    private Dictionary<string, string>? _typeAliases;
    private Dictionary<string, Type>? _nativeTypes;
    private HashSet<string>? _localEventNames;
    private HashSet<string>? _hushedCodes;

    /// <summary>Whether anything has been declared in this scope's tables.</summary>
    /// <remarks>
    /// Lets a scope-chain walk skip a scope without materialising its tables, which
    /// is what keeps the lazy tables from being allocated by the very lookups they
    /// exist to answer.
    /// </remarks>
    internal bool HasCommands => _commands is { Count: > 0 };

    internal bool HasClasses => _classes is { Count: > 0 };

    internal bool HasModules => _modules is { Count: > 0 };

    internal bool HasRefinementTypes => _refinementTypes is { Count: > 0 };

    internal bool HasNativeTypes => _nativeTypes is { Count: > 0 };

    internal bool HasHushedCodes => _hushedCodes is { Count: > 0 };

    /// <summary>
    /// CLR types emitted for <c>raw struct</c> declarations, keyed by their
    /// declared TōSh name.
    ///
    /// <see cref="TypeAliases"/> cannot hold these: it maps a name to a
    /// <em>path string</em> that the base resolver looks up, and a
    /// Reflection.Emit type has no discoverable path. <see cref="Classes"/>
    /// cannot either — it holds <c>IShellNamedType</c>, and its consumers cast.
    /// So emitted native types need their own registry, consulted first by
    /// <see cref="ScopedTypeResolver"/>.
    /// </summary>
    public Dictionary<string, Type> NativeTypes =>
        _nativeTypes ??= new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> LocalEventNames =>
        _localEventNames ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Diagnostic codes that should be hushed within this scope.
    /// Populated by the <c>hush</c> builtin; consulted by
    /// <c>ToshEngine.IsCodeHushed</c> when emitting warnings.
    /// </summary>
    public HashSet<string> HushedCodes =>
        _hushedCodes ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public bool IsModuleScope { get; }

    public bool ExportDeclarationsByDefault { get; }

    internal ModuleExportTable? Exports { get; }

    /// <summary>
    /// Creates an independent copy of this scope with its own variable, command, class,
    /// module, and type dictionaries. Used when forking an engine for concurrent execution
    /// so that writes in one fork do not affect the parent or sibling forks.
    /// </summary>
    public LexicalScope Clone()
    {
        var clone = new LexicalScope(Variables, IsModuleScope, ExportDeclarationsByDefault);
        foreach (var (key, value) in Commands) clone.Commands[key] = value;
        foreach (var (key, value) in Classes) clone.Classes[key] = value;
        foreach (var (key, value) in Modules) clone.Modules[key] = value;
        foreach (var (key, value) in RefinementTypes) clone.RefinementTypes[key] = value;
        foreach (var name in TypeImports) clone.TypeImports.Add(name);
        foreach (var (key, value) in TypeAliases) clone.TypeAliases[key] = value;
        foreach (var (key, value) in NativeTypes) clone.NativeTypes[key] = value;
        foreach (var name in LocalEventNames) clone.LocalEventNames.Add(name);
        foreach (var code in HushedCodes) clone.HushedCodes.Add(code);
        return clone;
    }
}
