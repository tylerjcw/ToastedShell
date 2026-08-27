namespace Tosh.Runtime;

/// <summary>
/// What a module offers, flattened for anything that reports on it.
/// </summary>
/// <param name="QualifiedName">
/// The name a caller writes, dotted through every enclosing module —
/// <c>ToastLib.Filesystem</c>, not <c>Filesystem</c>.
/// </param>
/// <param name="Commands">Exported command names, unqualified.</param>
/// <param name="Modules">Qualified names of the modules nested directly inside this one.</param>
/// <param name="Types">Exported type names, unqualified.</param>
/// <param name="Variables">Exported variable names, unqualified.</param>
/// <remarks>
/// `TS-P2-68`. A module is a language construct that <c>Tosh.Runtime</c> has no type for, so
/// <c>help</c> could not describe one even in principle. This is the flattened view the engine
/// hands over: enough to resolve a qualified name and to render a topic, without the help
/// catalogue needing to know what a module <em>is</em>.
/// </remarks>
public sealed record ShellModuleSummary(
    string QualifiedName,
    IReadOnlyList<string> Commands,
    IReadOnlyList<string> Modules,
    IReadOnlyList<string> Types,
    IReadOnlyList<string> Variables);
