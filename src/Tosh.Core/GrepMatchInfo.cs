namespace Tosh.Core;

public sealed record GrepMatchInfo(
    string? Path,
    int LineNumber,
    string Text,
    string Pattern);
