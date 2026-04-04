namespace Tosh.Tui.Requests;

/// <summary>Request yielded by <c>tui file</c> to launch a file/directory picker.</summary>
public sealed record TuiFilePickRequest(
    string? InitialPath = null,
    string? Filter = null,
    bool DirectoryOnly = false,
    bool ReturnOutcome = false);
