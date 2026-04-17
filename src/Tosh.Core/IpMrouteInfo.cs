using System.Net;

namespace Tosh.Core;

public sealed record IpMrouteInfo(
    string? Group,
    string? Source,
    string? Iif,
    IReadOnlyList<string> Oifs,
    int? Packets,
    long? Bytes);
