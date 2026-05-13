namespace Tosh.Client;

/// <summary>
/// Human-readable status lines. Always written to <c>/dev/tty</c> when
/// reachable so they interleave correctly with child output even when
/// the host shell is capturing our stdout. Falls back to
/// <see cref="Console.Error"/> when the tty isn't available.
/// </summary>
public sealed class ToshStatus
{
    internal ToshStatus() { }

    public void Info(string message) => Write(message);
    public void Warn(string message) => Write(message);
    public void Error(string message) => Write(message);

    public void InfoLine(string message) => WriteLine(message);
    public void WarnLine(string message) => WriteLine(message);
    public void ErrorLine(string message) => WriteLine(message);

    /// <summary>
    /// Generic write — appends a trailing newline. Public so callers can
    /// pre-format with their own colour codes.
    /// </summary>
    public void WriteLine(string message)
    {
        var line = message + "\n";
        if (TtyChannel.TryWrite(line)) return;
        Console.Error.WriteLine(message);
    }

    public void Write(string text)
    {
        if (TtyChannel.TryWrite(text)) return;
        Console.Error.Write(text);
    }
}
