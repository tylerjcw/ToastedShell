namespace Tosh.Tui.Requests;

/// <summary>Request yielded by <c>tui confirm</c> to launch a yes/no dialog.</summary>
public sealed record TuiConfirmRequest(
    string Message,
    string ConfirmLabel = "Yes",
    string CancelLabel = "No",
    bool DefaultConfirm = true,
    bool ReturnOutcome = false);
