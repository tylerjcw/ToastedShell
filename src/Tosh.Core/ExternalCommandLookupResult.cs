namespace Tosh.Core;

public sealed record ExternalCommandLookupResult(
    string Name,
    ExternalCommandLookupStatus Status,
    string? ResolvedPath,
    bool IsExplicitPath);
