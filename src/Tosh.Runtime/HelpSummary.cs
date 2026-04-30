namespace Tosh.Runtime;

public sealed record HelpSummary(
    string Name,
    HelpSubjectKind Kind,
    string Category,
    string Description,
    string Usage,
    IReadOnlyList<string> Aliases);
