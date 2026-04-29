using Tosh.Core;

namespace Tosh.Language;

internal sealed class ModuleExportTable
{
    public Dictionary<string, object?> Variables { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, IShellCommand> Commands { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, IShellNamedType> Types { get; } = new(StringComparer.Ordinal);

    internal Dictionary<string, RefinementTypeDefinition> RefinementTypes { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, object?> Modules { get; } = new(StringComparer.Ordinal);
}
