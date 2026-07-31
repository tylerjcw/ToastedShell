using Tosh.Runtime;

namespace Tosh.Language;

internal sealed class ModuleExportTable
{
    public Dictionary<string, object?> Variables { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, IShellCommand> Commands { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, IShellNamedType> Types { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Emitted CLR types for exported <c>raw struct</c>s. Kept alongside
    /// <see cref="Types"/> rather than inside it: consumers of <see cref="Types"/>
    /// expect an <c>IShellNamedType</c>, while the interop path needs the
    /// <see cref="Type"/> itself. One declaration populates both.
    /// </summary>
    public Dictionary<string, Type> NativeTypes { get; } = new(StringComparer.OrdinalIgnoreCase);

    internal Dictionary<string, RefinementTypeDefinition> RefinementTypes { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, object?> Modules { get; } = new(StringComparer.Ordinal);
}
