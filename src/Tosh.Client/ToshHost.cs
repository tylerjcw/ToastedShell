namespace Tosh.Client;

/// <summary>
/// Top-level entry point. Holds a cached <see cref="ToshHostInfo"/>
/// plus convenience accessors for the status, prompt, and frame-writing
/// channels.
///
/// Typical use:
/// <code>
/// var host = ToshHost.Current;
/// host.Status.InfoLine("Resolving dependencies...");
/// if (!host.Prompt.YesNo("Proceed?")) return;
/// using var frames = host.OpenFrameWriter("crumb.package");
/// frames.WriteRecord(new { Name = "tosh", Version = "1.0" });
/// </code>
/// </summary>
public sealed class ToshHost
{
    private static readonly Lazy<ToshHost> s_current = new(() => new ToshHost(ToshHostInfo.Detect()));

    public static ToshHost Current => s_current.Value;

    private readonly ToshStatus _status = new();
    private readonly ToshPrompt _prompt = new();

    internal ToshHost(ToshHostInfo info)
    {
        Info = info;
    }

    public ToshHostInfo Info { get; }

    /// <summary>True when stdout is being read by TōSh (TSSP-negotiated).</summary>
    public bool IsToshConsumer => Info.IsToshConsumer;

    public ToshStatus Status => _status;
    public ToshPrompt Prompt => _prompt;

    /// <summary>
    /// Open a frame writer over <see cref="Console.OpenStandardOutput"/>
    /// with the header pre-written. Caller disposes when done. Throws
    /// when <see cref="IsToshConsumer"/> is false — frames sent to a
    /// non-TōSh stdout would garble the user's terminal.
    /// </summary>
    public ToshFrameWriter OpenFrameWriter(string schema, IReadOnlyList<string>? modes = null, string? renderer = null)
    {
        if (!IsToshConsumer)
        {
            throw new InvalidOperationException(
                "TōSh has not negotiated structured stdout (TOSH_STRUCTURED_STDOUT != 1). " +
                "Use a plain-text renderer instead, or check ToshHost.Current.IsToshConsumer first.");
        }

        var stream = Console.OpenStandardOutput();
        var writer = new ToshFrameWriter(stream, leaveOpen: false);
        writer.WriteHeader(schema, modes, renderer);
        return writer;
    }
}
