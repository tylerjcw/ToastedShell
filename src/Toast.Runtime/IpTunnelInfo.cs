namespace Tosh.Runtime;

public sealed record IpTunnelInfo(
    string Name,
    string? Mode,
    string? Remote,
    string? Local,
    int? Ttl,
    string? Tos,
    bool Pmtudisc,
    string? Dev,
    string? InputKey,
    string? OutputKey);
