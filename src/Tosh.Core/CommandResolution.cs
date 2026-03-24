namespace Tosh.Core;

public sealed record CommandResolution(
    string Name,
    CommandResolutionKind Kind,
    string? Path,
    string? Description,
    string? Usage);
