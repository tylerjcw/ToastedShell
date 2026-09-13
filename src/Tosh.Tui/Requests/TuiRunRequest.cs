namespace Tosh.Tui.Requests;

/// <summary>Request yielded by <c>tui run</c> to launch a user-defined custom screen.</summary>
/// <param name="Screen">The screen definition the script built.</param>
/// <param name="ReturnOutcome">Whether to yield the outcome object rather than the selection.</param>
/// <param name="Tick">
/// Invokes the screen's tick handler. Built by <c>tui run</c> rather than taken from the
/// screen directly, because calling a script function needs a command context and the TUI
/// runtime has none — it should not grow one just to call back into the shell.
/// </param>
public sealed record TuiRunRequest(
    TuiScreen Screen,
    bool ReturnOutcome = false,
    Action? Tick = null);
