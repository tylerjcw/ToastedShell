using System.Collections;

namespace Tosh.Runtime;

/// <summary>
/// User-tunable knobs for the diagnostic system. Lives at <c>$tosh.Config.Diagnostics</c>.
/// </summary>
public sealed class ToshDiagnosticsConfig : IResettableShellConfig
{
    /// <summary>
    /// Codes (e.g. <c>tosh.runtime.fading_member</c>) that should be suppressed
    /// at process scope. Only diagnostics with severity <see cref="ToshDiagnosticSeverity.Warning"/>
    /// or lower can be hushed; errors always surface.
    /// </summary>
    public ToshHushedDiagnosticList Hushed { get; } = new();

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
public sealed class ToshHushedDiagnosticList : IResettableShellConfig, IEnumerable<string>
{
    private readonly HashSet<string> _codes = new(StringComparer.OrdinalIgnoreCase);

    public int Count => _codes.Count;

    public bool Contains(string code) => _codes.Contains(code);

    public bool Add(string code)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        return _codes.Add(code.Trim());
    }

    public bool Remove(string code)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        return _codes.Remove(code.Trim());
    }

    public void Clear() => _codes.Clear();

    public IEnumerator<string> GetEnumerator()
    {
        return _codes
            .OrderBy(static c => c, StringComparer.OrdinalIgnoreCase)
            .GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public IReadOnlyCollection<string> ToReadOnly()
    {
        return _codes
            .OrderBy(static c => c, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public void Reset() => _codes.Clear();

    public override string ToString() => $"[{string.Join(", ", this)}]";
}
