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
        Variables = new Dictionary<string, object?>(variables ?? new Dictionary<string, object?>(), StringComparer.Ordinal);
        Commands = new Dictionary<string, IShellCommand>(StringComparer.Ordinal);
        Classes = new Dictionary<string, object?>(StringComparer.Ordinal);
        Modules = new Dictionary<string, object?>(StringComparer.Ordinal);
        RefinementTypes = new Dictionary<string, RefinementTypeDefinition>(StringComparer.OrdinalIgnoreCase);
        TypeImports = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        TypeAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        NativeTypes = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);
        IsModuleScope = isModuleScope;
        ExportDeclarationsByDefault = exportDeclarationsByDefault;
        Exports = isModuleScope ? (exports ?? new ModuleExportTable()) : null;
    }

    public Dictionary<string, object?> Variables { get; }

    public Dictionary<string, IShellCommand> Commands { get; }

    public Dictionary<string, object?> Classes { get; }

    public Dictionary<string, object?> Modules { get; }

    internal Dictionary<string, RefinementTypeDefinition> RefinementTypes { get; }

    public HashSet<string> TypeImports { get; }

    public Dictionary<string, string> TypeAliases { get; }

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
    public Dictionary<string, Type> NativeTypes { get; }

    public HashSet<string> LocalEventNames { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Diagnostic codes that should be hushed within this scope.
    /// Populated by the <c>hush</c> builtin; consulted by
    /// <c>ToshEngine.IsCodeHushed</c> when emitting warnings.
    /// </summary>
    public HashSet<string> HushedCodes { get; } = new(StringComparer.OrdinalIgnoreCase);

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
