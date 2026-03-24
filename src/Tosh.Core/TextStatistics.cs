namespace Tosh.Core;

public sealed record TextStatistics(
    string? Path,
    int Lines,
    int Words,
    long Bytes,
    int Characters);
