namespace Tosh.Runtime;

/// <summary>
/// Creates the host command used when AutoCd resolves a command name to a directory.
/// </summary>
/// <remarks>
/// Recognizing a directory happens during language command resolution, but changing a
/// shell session also updates directory history and raises host events. Keeping the
/// command behind this factory removes that behavior from <c>Tosh.Language</c> while
/// allowing another host to define its own navigation semantics (`TOAST-0006`).
/// </remarks>
public interface IToastAutoCdCommandFactory
{
    IShellCommand CreateAutoCdCommand(string resolvedPath);
}
