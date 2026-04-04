namespace Tosh.Tui.Requests;

/// <summary>Request yielded by <c>help browse</c> to launch the help browser screen.</summary>
public sealed record HelpBrowseRequest(string? InitialQuery, string? InitialTopicName);
