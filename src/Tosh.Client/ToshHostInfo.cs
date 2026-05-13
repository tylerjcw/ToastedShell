namespace Tosh.Client;

/// <summary>
/// Snapshot of the TōSh host's negotiation envvars. Read once at process
/// start via <see cref="Detect"/> and reused; envvar changes mid-process
/// are not observed.
/// </summary>
public sealed class ToshHostInfo
{
    private static ToshHostInfo? s_cached;

    /// <summary>
    /// Detect the host once and cache the result. Subsequent calls return
    /// the same instance.
    /// </summary>
    public static ToshHostInfo Detect()
    {
        return s_cached ??= Build();
    }

    /// <summary>
    /// Re-read the environment and rebuild the snapshot. Mostly useful in tests.
    /// </summary>
    public static ToshHostInfo Refresh()
    {
        s_cached = Build();
        return s_cached;
    }

    private static ToshHostInfo Build()
    {
        var negotiated = Environment.GetEnvironmentVariable("TOSH_STRUCTURED_STDOUT") == "1";
        var consumer = ParseConsumer(Environment.GetEnvironmentVariable("TOSH_STDOUT_CONSUMER"));
        var stdio = ParseStdioMode(Environment.GetEnvironmentVariable("TOSH_STDIO_MODE"));
        var version = int.TryParse(Environment.GetEnvironmentVariable("TOSH_TSSP_VERSION"), out var v) ? v : 0;
        var width = int.TryParse(Environment.GetEnvironmentVariable("TOSH_TERM_WIDTH"), out var w) && w > 0 ? w : 80;
        var height = int.TryParse(Environment.GetEnvironmentVariable("TOSH_TERM_HEIGHT"), out var h) && h > 0 ? h : 24;
        var color = ParseColor(Environment.GetEnvironmentVariable("TOSH_COLOR"));
        var tty = Environment.GetEnvironmentVariable("TOSH_TTY");
        var accepts = (Environment.GetEnvironmentVariable("TOSH_STDIN_ACCEPTS") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return new ToshHostInfo(negotiated, consumer, stdio, version, width, height, color, tty, accepts);
    }

    private ToshHostInfo(
        bool isToshConsumer,
        ToshStdoutConsumer stdoutConsumer,
        ToshStdioMode stdioMode,
        int tsspVersion,
        int termWidth,
        int termHeight,
        ColorCapability color,
        string? controllingTty,
        IReadOnlyList<string> stdinAccepts)
    {
        IsToshConsumer = isToshConsumer;
        StdoutConsumer = stdoutConsumer;
        StdioMode = stdioMode;
        TsspVersion = tsspVersion;
        TermWidth = termWidth;
        TermHeight = termHeight;
        Color = color;
        ControllingTty = controllingTty;
        StdinAccepts = stdinAccepts;
    }

    /// <summary>True when <c>TOSH_STRUCTURED_STDOUT=1</c> — a TōSh pipeline is reading our stdout.</summary>
    public bool IsToshConsumer { get; }

    public ToshStdoutConsumer StdoutConsumer { get; }
    public ToshStdioMode StdioMode { get; }
    public int TsspVersion { get; }
    public int TermWidth { get; }
    public int TermHeight { get; }
    public ColorCapability Color { get; }
    public string? ControllingTty { get; }
    public IReadOnlyList<string> StdinAccepts { get; }

    /// <summary>True when stdin/stderr inherit a real TTY (passthrough or hybrid mode).</summary>
    public bool HasInteractiveTty => StdioMode is ToshStdioMode.Hybrid or ToshStdioMode.Passthrough
                                  || (StdioMode == ToshStdioMode.Unknown && !Console.IsInputRedirected);

    private static ToshStdoutConsumer ParseConsumer(string? raw) => raw switch
    {
        "pipe" => ToshStdoutConsumer.Pipe,
        "capture" => ToshStdoutConsumer.Capture,
        "terminal" => ToshStdoutConsumer.Terminal,
        _ => ToshStdoutConsumer.Unknown,
    };

    private static ToshStdioMode ParseStdioMode(string? raw) => raw switch
    {
        "passthrough" => ToshStdioMode.Passthrough,
        "hybrid" => ToshStdioMode.Hybrid,
        "pipe" => ToshStdioMode.Pipe,
        _ => ToshStdioMode.Unknown,
    };

    private static ColorCapability ParseColor(string? raw) => raw switch
    {
        "truecolor" => ColorCapability.TrueColor,
        "256" => ColorCapability.Ansi256,
        "16" or "ansi" => ColorCapability.Ansi16,
        "none" or "" or null => ColorCapability.None,
        _ => ColorCapability.None,
    };
}
