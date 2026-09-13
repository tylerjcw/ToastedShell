namespace Tosh.Runtime;

/// <summary>
/// Runs a full-screen TUI request and returns what the user chose.
/// </summary>
/// <remarks>
/// <para>
/// The standard library cannot reach a terminal, so the <c>tui</c> command used to yield
/// a request object and rely on a display sink further down to notice it, run it, and
/// substitute the result. That works when the value flows to the display and fails
/// silently when it does not:
/// </para>
/// <code>
/// var answer = tui confirm "Deploy now?"   # ANSWER=TuiConfirmRequest, and no dialog
/// </code>
/// <para>
/// With a runner on the runtime the command runs the screen itself and yields the
/// answer, so a result can be assigned, passed to a function, or used in a condition
/// like any other value (<c>TUI-0013</c>). This is the same shape as
/// <see cref="IInlinePromptProvider"/>: declared here, implemented by the CLI host,
/// absent in a headless process.
/// </para>
/// </remarks>
public interface ITuiScreenRunner
{
    /// <summary>Whether a terminal is available to draw on.</summary>
    bool CanRun { get; }

    /// <summary>
    /// Runs the screen for a request and reports its outcome.
    /// </summary>
    /// <returns>
    /// <see langword="false"/> when the request is not one this runner handles, leaving
    /// the caller to pass it on unchanged.
    /// </returns>
    bool TryRun(object request, out IReadOnlyList<object?>? results);
}
