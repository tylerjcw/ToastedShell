using Tosh.Client;

namespace Tosh.Crumb.Output;

/// <summary>
/// Thin shim over <see cref="ToshHost"/> preserving crumb's
/// historical <c>Confirm.Status</c> / <c>Confirm.YesNo</c> call sites.
/// All terminal I/O routes through <c>/dev/tty</c> via
/// <see cref="Tosh.Client.ToshStatus"/> and <see cref="Tosh.Client.ToshPrompt"/>.
/// </summary>
public static class Confirm
{
    public static void Status(string line) => ToshHost.Current.Status.WriteLine(line);

    public static bool YesNo(string question, bool defaultYes = true) =>
        ToshHost.Current.Prompt.YesNo(question, defaultYes);
}
