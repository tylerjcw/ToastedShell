using System.Net;
using System.Net.Sockets;

namespace Tosh.Runtime;

public sealed record IpAddressInfo(
    string Family,
    IPAddress Address,
    int PrefixLength,
    string? Scope,
    string? Label,
    IPAddress? Broadcast,
    bool Dynamic,
    bool NoPrefixRoute,
    long? ValidLifetimeSeconds,
    long? PreferredLifetimeSeconds)
{
    public string Cidr => $"{Address}/{PrefixLength}";

    public bool IsIpv4 => Address.AddressFamily == AddressFamily.InterNetwork;

    public bool IsIpv6 => Address.AddressFamily == AddressFamily.InterNetworkV6;

    public string ValidLifetime => FormatLifetime(ValidLifetimeSeconds);

    public string PreferredLifetime => FormatLifetime(PreferredLifetimeSeconds);

    private static string FormatLifetime(long? seconds)
    {
        return seconds switch
        {
            null => string.Empty,
            >= uint.MaxValue => "forever",
            _ => $"{seconds.Value} sec",
        };
    }
}
