namespace Tosh.Core;

public sealed record HelpSearchResult(
    string Name,
    double Score,
    HelpSubjectKind Kind,
    string Category,
    string Description,
    string Usage,
    IReadOnlyList<string> Aliases);
