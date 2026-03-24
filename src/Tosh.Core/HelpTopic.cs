namespace Tosh.Core;

public sealed record HelpTopic(
    string Name,
    HelpSubjectKind Kind,
    string Category,
    string Description,
    string Usage,
    IReadOnlyList<string> Aliases,
    IReadOnlyList<string> Related,
    IReadOnlyList<string> Examples,
    string? Path,
    string? Notes);
