namespace Tosh.Runtime;

/// <summary>
/// Where the language reports things it wants a human to see, without deciding how they
/// look or where they go.
/// </summary>
/// <remarks>
/// <para>
/// `TOAST-0006`, stage 2e. The language did all three jobs itself, in three places that
/// were identical apart from what they rendered:
/// </para>
/// <code>
/// var renderer = new DiagnosticRenderer(Runtime.Config.Theme.Diagnostics, Runtime.Config.Diagnostics);
/// Runtime.Error.WriteLine(renderer.RenderWarning(diagnostic));
/// </code>
/// <para>
/// That reads shell configuration for a theme, constructs a renderer, and writes to a
/// shell stream — none of which is a language concern. Reporting is; formatting and
/// destination are the host's.
/// </para>
/// <para>
/// **Warnings only, and errors deliberately not.** An error travels as a
/// `ToshDiagnosticException` and is rendered by whoever catches it, which is already the
/// host. A sink method for errors would offer a second route for something that must not
/// have two, since a diagnostic reported *and* thrown would be shown twice.
/// </para>
/// <para>
/// Trace is here rather than in a port of its own because it has the same shape — the
/// language decides there is something to say, the host decides whether and how it
/// appears. It is separated from warnings only so a host can route it differently, which
/// TōSh does not currently do but `set -x`-style tooling would.
/// </para>
/// </remarks>
public interface IToastDiagnosticSink
{
    /// <summary>Reports a warning-severity diagnostic the language produced.</summary>
    void ReportWarning(ToshDiagnostic diagnostic);

    /// <summary>
    /// Reports a warning that has no diagnostic behind it — a shadowed builtin, say,
    /// where the language has a title and some help but no span to point at.
    /// </summary>
    void ReportWarning(string title, string? help, string? info);

    /// <summary>
    /// Emits a trace line. The language decides there is something to trace; whether it
    /// is shown, and where, is the host's.
    /// </summary>
    ValueTask TraceAsync(string line, CancellationToken cancellationToken = default);
}
