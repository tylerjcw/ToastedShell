namespace Tosh.Client;

/// <summary>
/// Color capability advertised by the host (via <c>TOSH_COLOR</c>).
/// </summary>
public enum ColorCapability
{
    /// <summary>Plain text only — no escape sequences should be emitted.</summary>
    None = 0,
    /// <summary>Basic 16-colour ANSI.</summary>
    Ansi16 = 1,
    /// <summary>256-colour palette (<c>\e[38;5;Nm</c>).</summary>
    Ansi256 = 2,
    /// <summary>24-bit truecolor (<c>\e[38;2;R;G;Bm</c>).</summary>
    TrueColor = 3,
}

/// <summary>
/// How TōSh spawned this process (via <c>TOSH_STDIO_MODE</c>).
/// </summary>
public enum ToshStdioMode
{
    /// <summary>Not running under TōSh, or the host didn't advertise a mode.</summary>
    Unknown = 0,
    /// <summary>All three fds inherit the controlling TTY.</summary>
    Passthrough = 1,
    /// <summary>Stdin/stderr inherit the TTY; stdout is piped to TōSh's TSSP parser.</summary>
    Hybrid = 2,
    /// <summary>All three fds are piped/captured by TōSh.</summary>
    Pipe = 3,
}

/// <summary>
/// Which side of the pipeline is reading our stdout (via
/// <c>TOSH_STDOUT_CONSUMER</c>).
/// </summary>
public enum ToshStdoutConsumer
{
    Unknown = 0,
    /// <summary>Another pipeline stage is reading us.</summary>
    Pipe = 1,
    /// <summary>The output is being captured (e.g. into a variable).</summary>
    Capture = 2,
    /// <summary>TōSh itself will render our frames to the user's terminal.</summary>
    Terminal = 3,
}
