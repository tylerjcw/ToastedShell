namespace Tosh.Tui.Requests;

/// <summary>Request yielded by <c>tui run</c> to launch a user-defined custom screen.</summary>
public sealed record TuiRunRequest(
    TuiScreen Screen,
    bool ReturnOutcome = false);
