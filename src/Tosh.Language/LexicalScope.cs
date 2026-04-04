using Tosh.Core;

namespace Tosh.Language;

public sealed class LexicalScope
{
    public LexicalScope(
        IReadOnlyDictionary<string, object?>? variables = null,
        bool isModuleScope = false,
        bool exportDeclarationsByDefault = false)
    {
        Variables = new Dictionary<string, object?>(variables ?? new Dictionary<string, object?>(), StringComparer.Ordinal);
        Commands = new Dictionary<string, IShellCommand>(StringComparer.Ordinal);
        Classes = new Dictionary<string, object?>(StringComparer.Ordinal);
        Modules = new Dictionary<string, object?>(StringComparer.Ordinal);
        TypeImports = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        TypeAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        IsModuleScope = isModuleScope;
        ExportDeclarationsByDefault = exportDeclarationsByDefault;
        Exports = isModuleScope ? new ModuleExportTable() : null;
    }

    public Dictionary<string, object?> Variables { get; }

    public Dictionary<string, IShellCommand> Commands { get; }

    public Dictionary<string, object?> Classes { get; }

    public Dictionary<string, object?> Modules { get; }

    public HashSet<string> TypeImports { get; }

    public Dictionary<string, string> TypeAliases { get; }

    public HashSet<string> LocalEventNames { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool IsModuleScope { get; }

    public bool ExportDeclarationsByDefault { get; }

    internal ModuleExportTable? Exports { get; }
}
