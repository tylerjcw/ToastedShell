namespace Tosh.Runtime;

public sealed record IpTuntapInfo(
    string Name,
    string? Mode,
    string? Group,
    string? User,
    bool MultiQueue,
    IReadOnlyList<string> Flags);
