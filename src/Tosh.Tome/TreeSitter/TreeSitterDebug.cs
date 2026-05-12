namespace Tosh.Tome.TreeSitter;

/// <summary>
/// Tiny debug sink. When <c>TOME_TREESITTER_DEBUG=1</c>, log lines are
/// appended to <c>/tmp/tome-ts.log</c> (or <c>$TOME_TREESITTER_LOG</c>
/// when set). Writing to stderr would scramble the TUI, so we never do.
/// </summary>
internal static class TreeSitterDebug
{
    private static readonly bool _enabled =
        Environment.GetEnvironmentVariable("TOME_TREESITTER_DEBUG") == "1";

    private static readonly string _path =
        Environment.GetEnvironmentVariable("TOME_TREESITTER_LOG") ?? "/tmp/tome-ts.log";

    private static readonly Lock _gate = new();

    public static bool Enabled => _enabled;

    public static void Log(string message)
    {
        if (!_enabled) return;
        try
        {
            lock (_gate)
            {
                File.AppendAllText(_path, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
        }
        catch { /* logging must never throw */ }
    }
}
