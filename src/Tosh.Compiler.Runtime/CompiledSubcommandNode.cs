using Tosh.Language.Parsing;

namespace Tosh.Compiler.Runtime;

/// <summary>
/// Descriptor for a single flag or positional argument in a compiled
/// subcommand, carrying the pre-converted default value (if any) and
/// the type-name string used for argv coercion at runtime.
/// </summary>
public sealed record CompiledSubcommandParam(
    string Name,
    string? TypeName,
    bool IsOptional,
    bool IsRest,
    bool IsBool,
    bool HasDefault,
    object? DefaultValue);

/// <summary>
/// Compiled representation of one node in a subcommand dispatch tree.
/// Used by <see cref="ToshHost.RunCompiledSubcommandDispatch"/> to
/// route argv without replaying source text through the engine.
/// Each node carries its own flag/arg descriptors, a compiled body
/// delegate (flags-then-args bindings passed as <c>object?[]</c>),
/// and a dictionary of named child nodes for nested dispatch.
/// </summary>
public sealed class CompiledSubcommandNode
{
    /// <summary>Name of this subcommand, or <c>null</c> for the root.</summary>
    public string? Name { get; init; }

    /// <summary>Modifier flags (eager / hidden / hollow / vital).</summary>
    public SubcommandModifier Modifiers { get; init; }

    /// <summary>
    /// <c>true</c> when the user declared their own <c>--help</c>
    /// flag, suppressing the auto-help mechanism.
    /// </summary>
    public bool UserDeclaredHelpFlag { get; init; }

    /// <summary>Flag parameters for this node (from <c>flag</c> declarations).</summary>
    public required CompiledSubcommandParam[] Flags { get; init; }

    /// <summary>Positional parameters for this node (from <c>arg</c>/<c>args</c> declarations).</summary>
    public required CompiledSubcommandParam[] Args { get; init; }

    /// <summary>Named child subcommand nodes, keyed by subcommand name (ordinal).</summary>
    public required Dictionary<string, CompiledSubcommandNode> Children { get; init; }

    /// <summary>
    /// Compiled body delegate.  Receives an <c>object?[]</c> where the
    /// first <c>Flags.Length</c> slots are flag-binding values (in
    /// declaration order) and the remaining slots are positional-arg
    /// values.  <c>null</c> means the node has no body to execute.
    /// </summary>
    public Action<object?[]>? Body { get; init; }
}
