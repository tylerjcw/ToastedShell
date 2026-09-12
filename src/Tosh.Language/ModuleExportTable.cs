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

    /// <summary>
    /// The module's own dotted path — <c>ToastLib.Math</c> — as it was declared.
    /// </summary>
    /// <remarks>
    /// `TOAST-0122`. The keys in this table are bare names, so a declaration that
    /// annotates its own types by their full path — which is good practice, and what
    /// the author's library does — could not be resolved against it. Knowing the path
    /// lets that prefix be recognised and stripped, and *only* that prefix: a name
    /// that does not begin with this module's path is left alone rather than guessed at
    /// by matching its last segment.
    /// </remarks>
    internal string? QualifiedName { get; set; }
}
