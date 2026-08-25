using Tosh.Language.Debugging;

namespace Tosh.Language;

/// <summary>
/// The script-running capabilities a host exposes to commands that drive it.
/// </summary>
/// <remarks>
/// <para>
/// `TOAST-0006`. The engine used to construct and register `source`, `eval` and `debug`
/// itself, which made the *language* own a set of commands — and a command is a shell
/// concept. The language should expose what it can do and let TōSh decide what to call
/// it, which is what this interface is for.
/// </para>
/// <para>
/// It is separate from <see cref="Tosh.Runtime.IShellEvaluator"/> rather than added to
/// it, and the reason is concrete: `IShellEvaluator` lives in `Tosh.Runtime`, while
/// `DebugStepContext` exposes a <c>StatementSyntax</c> — a parser type. Putting the
/// debug hook on the runtime interface would drag the parser into the runtime, so the
/// capability that needs language types is declared where the language types already
/// are.
/// </para>
/// <para>
/// Commands reach it through <c>context.LanguageRuntime.Evaluator as IToshScriptHost</c>, which
/// is the pattern `XargsCommand`, `VarsCommand` and `ExportCommand` already use for
/// <see cref="Tosh.Runtime.IShellEvaluator"/>. Resolving at execute time rather than at
/// construction is what lets these commands be registered before an engine exists.
/// </para>
/// </remarks>
public interface IToshScriptHost
{
    /// <summary>
    /// Resolves a script path the way `source` does — a relative path means "beside the
    /// script doing the sourcing" rather than beside the process working directory
    /// (`TS-P2-29`).
    /// </summary>
    string ResolveSourcePath(string rawPath);

    /// <summary>
    /// Runs a script file.
    /// </summary>
    /// <param name="isolateScope">
    /// <see langword="false"/> lets the script affect the caller's scope, which is what
    /// makes `source` different from running a script as a child.
    /// </param>
    IAsyncEnumerable<object?> ExecuteScriptFileAsync(
        string path,
        IReadOnlyList<object?>? arguments,
        bool isolateScope,
        CancellationToken cancellationToken);

    /// <summary>
    /// The hook invoked between statements, or <see langword="null"/> when nothing is
    /// stepping. Settable so a debugger can install itself and restore what was there.
    /// </summary>
    DebugHookDelegate? DebugHook { get; set; }
}
