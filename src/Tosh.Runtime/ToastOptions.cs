namespace Tosh.Runtime;

/// <summary>
/// Settings the **language** needs, owned by the language and independent of any shell.
/// </summary>
/// <remarks>
/// <para>
/// `TOAST-0006`, stage 1. The rule is that nothing under <c>~/.config/tosh</c> may affect
/// Tōast — that directory configures TōSh. The rule was **not held**, in a way that is
/// easy to miss: `Tosh.Language` never reads the config *directory*, which was checked
/// and is true, but it read values the shell had loaded from one.
/// </para>
/// <para>
/// Two of them are language concerns wearing shell clothing.
/// <c>Config.Shell.MaxRecursionDepth</c> is a limit on the evaluator, filed under
/// "Shell"; <c>Config.Diagnostics.Hushed</c> backs <c>hush</c>, which is language syntax.
/// A host embedding Tōast with no TōSh had no answer for what either should be.
/// </para>
/// <para>
/// **This type is the authority, not a copy.** `ToshShellConfig.MaxRecursionDepth` and
/// `ToshDiagnosticsConfig.Hushed` delegate here rather than holding their own storage,
/// because both are changed at runtime — from script, via
/// <c>$tosh.Config.Shell.MaxRecursionDepth = 5</c>, and `RecursionDepthTests` asserts the
/// change takes effect. A snapshot taken at startup would compile, pass a casual reading,
/// and silently stop honouring assignments.
/// </para>
/// </remarks>
public sealed class ToastOptions
{
    private int _maxRecursionDepth = ToshExecutionDepthGuard.DefaultMaximumDepth;

    /// <summary>
    /// How deep evaluation may recurse before the depth guard reports. A language limit:
    /// it exists to turn a runaway recursion into a diagnostic rather than a stack
    /// overflow, which is true whether or not a shell is present.
    /// </summary>
    public int MaxRecursionDepth
    {
        get => _maxRecursionDepth;
        set
        {
            ToshExecutionDepthGuard.ValidateMaximumDepth(value);
            _maxRecursionDepth = value;
        }
    }

    /// <summary>
    /// Diagnostic codes suppressed by <c>hush</c>. Language syntax, so the set belongs to
    /// the language even though TōSh also exposes it as <c>$tosh.Config.Diagnostics.Hushed</c>.
    /// </summary>
    public ToshHushedDiagnosticList HushedDiagnostics { get; } = new();

    /// <summary>
    /// Whether <paramref name="code"/> is suppressed at <paramref name="severity"/>.
    /// </summary>
    /// <remarks>
    /// Errors are never hushed, and that is deliberate rather than an oversight: `hush`
    /// is for noise, and a rule that could silence an error would make a failing script
    /// look like a passing one.
    /// </remarks>
    public bool IsHushed(string? code, ToshDiagnosticSeverity severity)
    {
        if (severity == ToshDiagnosticSeverity.Error)
        {
            return false;
        }

        return code is not null && HushedDiagnostics.Contains(code);
    }
}
