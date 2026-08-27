namespace Tosh.Runtime;

/// <summary>
/// Turns a script's own help description into whatever the host renders — <c>TOAST-0006</c>.
/// </summary>
/// <remarks>
/// The language can say what a script's interface is; only the host knows how help is
/// presented. TōSh answers with a <c>HelpTopic</c>, so a script's <c>--help</c> is drawn by
/// the same panel renderer as every other topic. A host that supplies no factory leaves the
/// language yielding its own <see cref="ToastScriptHelp"/>.
/// </remarks>
public interface IToastScriptHelpFactory
{
    /// <summary>The host's representation of <paramref name="help"/>.</summary>
    object CreateScriptHelpTopic(ToastScriptHelp help);
}
