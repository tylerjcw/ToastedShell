namespace Tosh.Tui.Requests;

/// <summary>Request yielded by <c>config browse</c> to launch the config browser screen.</summary>
public sealed record ConfigBrowseRequest(string? InitialQuery, string? InitialPath);
