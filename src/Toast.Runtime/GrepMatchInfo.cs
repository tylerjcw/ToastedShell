namespace Tosh.Runtime;

public sealed record GrepMatchInfo(
    string? Path,
    int LineNumber,
    string Text,
    string Pattern,
    string? Match = null,
    IReadOnlyList<string>? ContextBefore = null,
    IReadOnlyList<string>? ContextAfter = null);
