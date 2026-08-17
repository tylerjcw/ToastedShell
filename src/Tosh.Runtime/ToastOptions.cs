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
    /// A pipeline's exit status is the first non-zero stage's rather than the last
    /// stage's — `set -o pipefail`.
    /// </summary>
    /// <remarks>
    /// A pipeline is language syntax in Tōast, so how its exit status is computed is
    /// language semantics. It arrived as a shell option because that is where pipelines
    /// came from, not because a shell is required to have an opinion about it.
    /// </remarks>
    public bool Pipefail { get; set; }

    /// <summary>Stop executing after a stage exits non-zero — `set -e`.</summary>
    public bool ExitOnError { get; set; }

    /// <summary>Emit each command before running it — `set -x`.</summary>
    public bool Trace { get; set; }

    /// <summary>Emit each statement of a script before running it.</summary>
    public bool ScriptTrace { get; set; }

    /// <summary>
    /// A bare word naming a directory changes to it instead of reporting an unknown
    /// command.
    /// </summary>
    /// <remarks>
    /// The most debatable of these. It arrived as a shell convenience — zsh's `AUTO_CD` —
    /// and it is the one a reader is most likely to call shell behaviour. It is here
    /// because it is a *dispatch rule*: it decides what an unresolved name means, which
    /// the language does. A host with no session can leave it off, which is the default.
    /// </remarks>
    public bool AutoCd { get; set; }

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
