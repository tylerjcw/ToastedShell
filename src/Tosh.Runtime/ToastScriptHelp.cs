namespace Tosh.Runtime;

/// <summary>
/// A script's description of its own interface, in language-owned terms — <c>TOAST-0006</c>.
/// </summary>
/// <remarks>
/// <para>
/// A script built from <c>subcommand</c> blocks knows its own arguments, flags and children,
/// and <c>script.tosh sub --help</c> has to describe them. The language built a
/// <c>HelpTopic</c> to do it, which is shell help metadata — 58 uses in the shell runtime and
/// 13 in the standard library against 3 in the language, so the odd one out was the language.
/// </para>
/// <para>
/// The language describes; the host decides what that becomes. TōSh maps this to a
/// <c>HelpTopic</c> so the panel renderer draws it exactly as <c>help &lt;name&gt;</c> is
/// drawn, and a language-only host with no factory receives this record itself, which still
/// renders as a value.
/// </para>
/// </remarks>
public sealed record ToastScriptHelp(
    string Name,
    string Description,
    string Usage,
    IReadOnlyList<string> Examples,
    IReadOnlyList<ToastScriptHelpArgument>? Arguments = null,
    IReadOnlyList<ToastScriptHelpOption>? Options = null,
    IReadOnlyList<ToastScriptHelpArgument>? Subcommands = null);

/// <summary>One positional argument, or one child subcommand named and described.</summary>
public sealed record ToastScriptHelpArgument(
    string Name,
    string Description,
    bool Required = true,
    string? TypeName = null);

/// <summary>One flag, already spelled the way the script declares it.</summary>
public sealed record ToastScriptHelpOption(
    string Syntax,
    string Description);
