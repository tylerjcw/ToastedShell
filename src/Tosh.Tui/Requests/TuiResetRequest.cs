namespace Tosh.Tui.Requests;

/// <summary>
/// Request yielded by <c>tui reset</c>: put this terminal back.
/// </summary>
/// <remarks>
/// A request rather than a write, like every other <c>tui</c> subcommand, so the command
/// stays a description of what was asked for and the terminal stays the host's to touch.
/// </remarks>
public sealed record TuiResetRequest;
