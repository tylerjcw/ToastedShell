using System.Net;

namespace Tosh.Core;

public sealed record IpRouteInfo(
    string Destination,
    IPAddress? Gateway,
    string? Device,
    string? Protocol,
    string? Scope,
    IPAddress? PreferredSource,
    long? Metric,
    string? Preference,
    string? Table,
    string? RouteType,
    IReadOnlyList<string> Flags)
{
    public bool IsDefault =>
        string.Equals(Destination, "default", StringComparison.OrdinalIgnoreCase);

    public bool IsIpv6 =>
        Destination.Contains(':', StringComparison.Ordinal) ||
        Gateway?.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ||
        PreferredSource?.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6;

    public bool IsIpv4 => !IsIpv6;

    public string FlagsText => string.Join(", ", Flags);
}
