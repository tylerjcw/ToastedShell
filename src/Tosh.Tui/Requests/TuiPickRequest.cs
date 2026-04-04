namespace Tosh.Tui.Requests;

/// <summary>Request yielded by <c>tui pick</c> to launch a list picker screen.</summary>
public sealed record TuiPickRequest(
    IReadOnlyList<object?> Items,
    string? DisplayProperty = null,
    string? Prompt = null,
    bool MultiSelect = false,
    bool ReturnOutcome = false);
