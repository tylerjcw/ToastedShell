using Tosh.Cli.Tui;
using Tosh.Runtime;
using Tosh.Tui;

namespace Tosh.Cli;

/// <summary>
/// Runs TUI requests against the console, so the <c>tui</c> command can return an answer
/// rather than a request object.
/// </summary>
/// <remarks>
/// A thin adapter over <see cref="TuiRequestDispatcher"/>: the screens it builds live
/// here, in the executable, while the command that wants them lives in the standard
/// library. This is the seam between the two.
/// </remarks>
internal sealed class ConsoleTuiScreenRunner(ToshRuntime runtime) : ITuiScreenRunner
{
    /// <inheritdoc />
    public bool CanRun => new ConsoleTuiHost().IsInteractive;

    /// <inheritdoc />
    public bool TryRun(object request, out IReadOnlyList<object?>? results)
    {
        ArgumentNullException.ThrowIfNull(request);

        return TuiRequestDispatcher.TryHandle([request], runtime, out results);
    }
}
