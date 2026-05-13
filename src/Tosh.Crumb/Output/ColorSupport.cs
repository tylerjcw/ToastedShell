namespace Tosh.Crumb.Output;

/// <summary>
/// Centralised colour gating + truecolor detection for crumb output.
/// Two contexts call in:
///  - <see cref="PackageFormatter"/> writes to stdout, so it disables
///    colour when stdout is redirected.
///  - <see cref="UpgradeListFormatter"/> writes through
///    <see cref="Confirm.Status"/> (i.e. /dev/tty when reachable),
///    so it gates on stderr-tty + the TōSh hybrid <c>TOSH_TTY</c> hint
///    instead of the stdout pipe.
/// </summary>
internal static class ColorSupport
{
    /// <summary>Colour gating for output that lands on stdout.</summary>
    public static bool StdoutColorEnabled()
    {
        if (string.Equals(Environment.GetEnvironmentVariable("NO_COLOR"), "1", StringComparison.Ordinal)) return false;
        if (string.Equals(Environment.GetEnvironmentVariable("CRUMB_NO_COLOR"), "1", StringComparison.Ordinal)) return false;
        return !Console.IsOutputRedirected;
    }

    /// <summary>Colour gating for output that lands on /dev/tty / stderr.</summary>
    public static bool StatusColorEnabled()
    {
        if (string.Equals(Environment.GetEnvironmentVariable("NO_COLOR"), "1", StringComparison.Ordinal)) return false;
        if (string.Equals(Environment.GetEnvironmentVariable("CRUMB_NO_COLOR"), "1", StringComparison.Ordinal)) return false;
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TOSH_TTY"))) return true;
        return !Console.IsErrorRedirected;
    }

    /// <summary>
    /// True when the terminal advertises 24-bit colour via
    /// <c>COLORTERM=truecolor|24bit</c> or a <c>-direct</c>/<c>-truecolor</c>
    /// TERM. <c>CRUMB_NO_TRUECOLOR=1</c> forces the 256-colour fallback.
    /// </summary>
    public static bool SupportsTrueColor()
    {
        if (string.Equals(Environment.GetEnvironmentVariable("CRUMB_NO_TRUECOLOR"), "1", StringComparison.Ordinal))
            return false;
        var colorterm = Environment.GetEnvironmentVariable("COLORTERM");
        if (colorterm is "truecolor" or "24bit") return true;
        var term = Environment.GetEnvironmentVariable("TERM");
        return term is not null
            && (term.EndsWith("-direct", StringComparison.Ordinal)
                || term.EndsWith("-truecolor", StringComparison.Ordinal));
    }
}
