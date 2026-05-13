namespace Tosh.Client;

/// <summary>
/// Interactive prompts that work even when stdout is captured by a host
/// shell. Each call opens <c>/dev/tty</c> fresh, drains stale input, and
/// reads byte-by-byte via <c>read(2)</c>.
/// </summary>
public sealed class ToshPrompt
{
    internal ToshPrompt() { }

    /// <summary>
    /// Display <paramref name="question"/> with a <c>[Y/n]</c> or
    /// <c>[y/N]</c> suffix and return the user's choice. When no
    /// interactive channel is reachable (and stdin is not a tty either)
    /// the default is returned.
    /// </summary>
    public bool YesNo(string question, bool defaultYes = true)
    {
        var suffix = defaultYes ? " [Y/n] " : " [y/N] ";
        WritePrompt(question + suffix);

        var line = TtyChannel.TryReadLine();
        if (line is null)
        {
            if (Console.IsInputRedirected) return defaultYes;
            try { line = Console.In.ReadLine(); }
            catch { line = null; }
        }
        return ParseAnswer(line, defaultYes);
    }

    /// <summary>
    /// Display <paramref name="question"/> and read one line of input.
    /// Returns null when no interactive channel is reachable.
    /// </summary>
    public string? Line(string question)
    {
        WritePrompt(question);
        var line = TtyChannel.TryReadLine();
        if (line is null && !Console.IsInputRedirected)
        {
            try { line = Console.In.ReadLine(); }
            catch { line = null; }
        }
        return line;
    }

    private static void WritePrompt(string text)
    {
        if (TtyChannel.TryWrite(text)) return;
        Console.Error.Write(text);
        try { Console.Error.Flush(); } catch { }
    }

    private static bool ParseAnswer(string? line, bool defaultYes)
    {
        if (line is null) return defaultYes;
        var trimmed = line.Trim();
        if (trimmed.Length == 0) return defaultYes;
        return trimmed[0] is 'y' or 'Y';
    }
}
