using System.Collections;

namespace Tosh.Runtime;

/// <summary>
/// User-tunable knobs for the diagnostic system. Lives at <c>$tosh.Config.Diagnostics</c>.
/// </summary>
public sealed class ToshDiagnosticsConfig : IResettableShellConfig
{
    private readonly ToastOptions _options;

    public ToshDiagnosticsConfig(ToastOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <summary>
    /// Codes (e.g. <c>tosh.runtime.fading_member</c>) that should be suppressed
    /// at process scope. Only diagnostics with severity <see cref="ToshDiagnosticSeverity.Warning"/>
    /// or lower can be hushed; errors always surface.
    /// </summary>
    /// <summary>
    /// Suppressed diagnostic codes. Storage lives on <see cref="ToastOptions"/>; `hush`
    /// is language syntax, and this is the shell's view of it (`TOAST-0006`).
    /// </summary>
    public ToshHushedDiagnosticList Hushed => _options.HushedDiagnostics;

    /// <summary>
    /// Optional base URL used to render diagnostic codes as hyperlinks
    /// (e.g. <c>https://tosh.dev/d/</c>). When null, codes render as plain text.
    /// </summary>
    public string? HelpUriBase { get; set; }

    /// <summary>
    /// When true, force the plain ASCII renderer even on a TTY.
    /// Equivalent to setting <c>TOSH_DIAG_PLAIN=1</c>.
    /// </summary>
    public bool PlainOutput { get; set; }

    /// <summary>
    /// Output format for diagnostics. <c>Text</c> uses the styled renderer
    /// (TTY) or its plain fallback (non-TTY); <c>Json</c> emits one
    /// NDJSON line per diagnostic to stderr.
    /// </summary>
    public ToshDiagnosticFormat Format { get; set; } = ToshDiagnosticFormat.Text;

    public void Reset()
    {
        Hushed.Reset();
        HelpUriBase = null;
        PlainOutput = false;
        Format = ToshDiagnosticFormat.Text;
    }

    /// <summary>
    /// Returns <c>true</c> if a diagnostic with the given <paramref name="code"/>
    /// and <paramref name="severity"/> should be suppressed by the global hush list.
    /// Errors are never suppressible regardless of configuration.
    /// </summary>
    public bool IsHushed(string? code, ToshDiagnosticSeverity severity)
    {
        if (severity == ToshDiagnosticSeverity.Error)
        {
            return false;
        }
        return code is not null && Hushed.Contains(code);
    }
}

public enum ToshDiagnosticFormat
{
    Text = 0,
    Json,
}

/// <summary>
/// Mutable, case-insensitive set of suppressed diagnostic codes. Exposed to
/// TōSh as a list-shaped collection so users can <c>add</c>, <c>remove</c>, and
/// iterate it from script.
/// </summary>
