namespace Tosh.Tui.Requests;

/// <summary>Request yielded by <c>tui input</c> to launch a text input screen.</summary>
public sealed record TuiInputRequest(
    string? Prompt = null,
    string? DefaultValue = null,
    bool Multiline = false,
    bool ReturnOutcome = false);
