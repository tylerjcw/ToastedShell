using System.Net;
using System.Text.Json;

namespace Tosh.Runtime;

public static class IpJsonParser
{
    public static IReadOnlyList<IpInterfaceInfo> ParseInterfaces(string json)
    {
        return EnumerateRootObjects(json)
            .Select(ParseInterface)
            .ToArray();
    }

    public static IReadOnlyList<IpRouteInfo> ParseRoutes(string json)
    {
        return EnumerateRootObjects(json)
            .Select(ParseRoute)
            .ToArray();
    }

    public static IReadOnlyList<IpNeighborInfo> ParseNeighbors(string json)
    {
        return EnumerateRootObjects(json)
            .Select(ParseNeighbor)
            .ToArray();
    }

    public static IReadOnlyList<IpRuleInfo> ParseRules(string json)
    {
        return EnumerateRootObjects(json)
            .Select(ParseRule)
            .ToArray();
    }

    public static IReadOnlyList<IpNetnsInfo> ParseNamespaces(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<IpNetnsInfo>();
        }

        return EnumerateRootObjects(json)
            .Select(ParseNamespace)
            .ToArray();
    }

    public static IReadOnlyList<IpTunnelInfo> ParseTunnels(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<IpTunnelInfo>();
        }

        return EnumerateRootObjects(json)
            .Select(ParseTunnel)
            .ToArray();
    }

    public static IReadOnlyList<IpTuntapInfo> ParseTuntaps(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<IpTuntapInfo>();
        }

        return EnumerateRootObjects(json)
            .Select(ParseTuntap)
            .ToArray();
    }

    public static IReadOnlyList<IpVrfInfo> ParseVrfs(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<IpVrfInfo>();
        }

        return EnumerateRootObjects(json)
            .Select(ParseVrf)
            .ToArray();
    }

    public static IReadOnlyList<IpMaddrInfo> ParseMaddrs(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<IpMaddrInfo>();
        }

        return EnumerateRootObjects(json)
            .Select(ParseMaddr)
            .ToArray();
    }

    public static IReadOnlyList<IpMrouteInfo> ParseMroutes(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<IpMrouteInfo>();
        }

        return EnumerateRootObjects(json)
            .Select(ParseMroute)
            .ToArray();
    }

    public static IReadOnlyList<IpTokenInfo> ParseTokens(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<IpTokenInfo>();
        }

        return EnumerateRootObjects(json)
            .Select(ParseToken)
            .ToArray();
    }

    public static IReadOnlyList<IpNtableInfo> ParseNtables(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<IpNtableInfo>();
        }

        return EnumerateRootObjects(json)
            .Select(ParseNtable)
            .ToArray();
    }

    private static IpInterfaceInfo ParseInterface(JsonElement element)
    {
        return new IpInterfaceInfo(
            Index: GetInt32(element, "ifindex") ?? 0,
            Name: GetString(element, "ifname") ?? string.Empty,
            Flags: GetStringArray(element, "flags"),
            Mtu: GetInt32(element, "mtu"),
            QueueDiscipline: GetString(element, "qdisc"),
            State: GetString(element, "operstate"),
            Group: GetString(element, "group"),
            QueueLength: GetInt32(element, "txqlen"),
            LinkType: GetString(element, "link_type"),
            HardwareAddress: GetString(element, "address"),
            BroadcastAddress: GetString(element, "broadcast"),
            PermanentAddress: GetString(element, "permaddr"),
            AltNames: GetStringArray(element, "altnames"),
            Addresses: ParseAddresses(element));
    }

    private static IReadOnlyList<IpAddressInfo> ParseAddresses(JsonElement interfaceElement)
    {
        if (!interfaceElement.TryGetProperty("addr_info", out var addressInfo) ||
            addressInfo.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<IpAddressInfo>();
        }

        var addresses = new List<IpAddressInfo>();

        foreach (var element in addressInfo.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var rawAddress = GetString(element, "local");

            if (string.IsNullOrWhiteSpace(rawAddress) || !IPAddress.TryParse(rawAddress, out var address))
            {
                continue;
            }

            addresses.Add(new IpAddressInfo(
                Family: GetString(element, "family") ?? string.Empty,
                Address: address,
                PrefixLength: GetInt32(element, "prefixlen") ?? 0,
                Scope: GetString(element, "scope"),
                Label: GetString(element, "label"),
                Broadcast: ParseOptionalIpAddress(GetString(element, "broadcast")),
                Dynamic: GetBoolean(element, "dynamic"),
                NoPrefixRoute: GetBoolean(element, "noprefixroute"),
                ValidLifetimeSeconds: GetInt64(element, "valid_life_time"),
                PreferredLifetimeSeconds: GetInt64(element, "preferred_life_time")));
        }

        return addresses;
    }

    private static IpRouteInfo ParseRoute(JsonElement element)
    {
        return new IpRouteInfo(
            Destination: GetString(element, "dst") ?? "default",
            Gateway: ParseOptionalIpAddress(GetString(element, "gateway")),
            Device: GetString(element, "dev"),
            Protocol: GetString(element, "protocol"),
            Scope: GetString(element, "scope"),
            PreferredSource: ParseOptionalIpAddress(GetString(element, "prefsrc")),
            Metric: GetInt64(element, "metric"),
            Preference: GetString(element, "pref"),
            Table: GetString(element, "table"),
            RouteType: GetString(element, "type"),
            Flags: GetStringArray(element, "flags"));
    }

    private static IpNeighborInfo ParseNeighbor(JsonElement element)
    {
        return new IpNeighborInfo(
            Address: ParseOptionalIpAddress(GetString(element, "dst")),
            Device: GetString(element, "dev"),
            LinkLayerAddress: GetString(element, "lladdr"),
            State: GetStringArray(element, "state"));
    }

    private static IpRuleInfo ParseRule(JsonElement element)
    {
        return new IpRuleInfo(
            Priority: GetInt32(element, "priority"),
            Source: GetString(element, "src"),
            Table: GetString(element, "table"),
            Action: GetString(element, "action"),
            Destination: GetString(element, "dst"),
            IifName: GetString(element, "iifname"),
            OifName: GetString(element, "oifname"),
            FirewallMark: GetInt32(element, "fwmark"),
            Protocol: GetString(element, "protocol"));
    }

    private static IpNetnsInfo ParseNamespace(JsonElement element)
    {
        return new IpNetnsInfo(
            Name: GetString(element, "name") ?? string.Empty,
            Id: GetInt32(element, "id"));
    }

    private static IpTunnelInfo ParseTunnel(JsonElement element)
    {
        return new IpTunnelInfo(
            Name: GetString(element, "ifname") ?? GetString(element, "name") ?? string.Empty,
            Mode: GetString(element, "mode"),
            Remote: GetString(element, "remote"),
            Local: GetString(element, "local"),
            Ttl: GetInt32(element, "ttl"),
            Tos: GetString(element, "tos"),
            Pmtudisc: GetBoolean(element, "pmtudisc"),
            Dev: GetString(element, "dev"),
            InputKey: GetString(element, "ikey"),
            OutputKey: GetString(element, "okey"));
    }

    private static IpTuntapInfo ParseTuntap(JsonElement element)
    {
        return new IpTuntapInfo(
            Name: GetString(element, "ifname") ?? string.Empty,
            Mode: GetString(element, "mode"),
            Group: GetString(element, "group"),
            User: GetString(element, "user"),
            MultiQueue: GetBoolean(element, "multi_queue"),
            Flags: GetStringArray(element, "flags"));
    }

    private static IpVrfInfo ParseVrf(JsonElement element)
    {
        return new IpVrfInfo(
            Name: GetString(element, "name") ?? string.Empty,
            TableId: GetInt32(element, "table"));
    }

    private static IpMaddrInfo ParseMaddr(JsonElement element)
    {
        var addresses = new List<IpMaddrEntry>();

        if (element.TryGetProperty("maddr", out var maddrArray) &&
            maddrArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in maddrArray.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                addresses.Add(new IpMaddrEntry(
                    Family: GetString(entry, "family"),
                    Address: GetString(entry, "address"),
                    Link: GetString(entry, "link"),
                    Users: GetInt32(entry, "users")));
            }
        }

        return new IpMaddrInfo(
            Index: GetInt32(element, "ifindex") ?? 0,
            Name: GetString(element, "ifname") ?? string.Empty,
            Addresses: addresses);
    }

    private static IpMrouteInfo ParseMroute(JsonElement element)
    {
        var oifs = new List<string>();

        if (element.TryGetProperty("oifs", out var oifsArray) &&
            oifsArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var oifElement in oifsArray.EnumerateArray())
            {
                var dev = oifElement.ValueKind == JsonValueKind.Object
                    ? GetString(oifElement, "dev")
                    : oifElement.ValueKind == JsonValueKind.String
                        ? oifElement.GetString()
                        : null;

                if (!string.IsNullOrWhiteSpace(dev))
                {
                    oifs.Add(dev);
                }
            }
        }

        return new IpMrouteInfo(
            Group: GetString(element, "group") ?? GetString(element, "dst"),
            Source: GetString(element, "src"),
            Iif: GetString(element, "iif"),
            Oifs: oifs,
            Packets: GetInt32(element, "packets"),
            Bytes: GetInt64(element, "bytes"));
    }

    private static IpTokenInfo ParseToken(JsonElement element)
    {
        return new IpTokenInfo(
            Token: GetString(element, "token") ?? string.Empty,
            InterfaceName: GetString(element, "ifname"));
    }

    private static IpNtableInfo ParseNtable(JsonElement element)
    {
        return new IpNtableInfo(
            Family: GetString(element, "family"),
            Name: GetString(element, "name"),
            Dev: GetString(element, "dev"),
            Thresh1: GetInt32(element, "thresh1"),
            Thresh2: GetInt32(element, "thresh2"),
            Thresh3: GetInt32(element, "thresh3"),
            GcInterval: GetInt32(element, "gc_interval"),
            RefCount: GetInt32(element, "refcnt"),
            Reachable: GetInt32(element, "reachable"),
            BaseReachable: GetInt32(element, "base_reachable"),
            Retrans: GetInt32(element, "retrans"),
            GcStale: GetInt32(element, "gc_stale"),
            DelayProbe: GetInt32(element, "delay_probe"),
            Queue: GetInt32(element, "queue"),
            AppProbes: GetInt32(element, "app_probes"),
            UnicastProbes: GetInt32(element, "ucast_probes"),
            MulticastProbes: GetInt32(element, "mcast_probes"),
            MulticastReprobes: GetInt32(element, "mcast_reprobes"),
            AnycastDelay: GetInt32(element, "anycast_delay"),
            ProxyDelay: GetInt32(element, "proxy_delay"),
            ProxyQueue: GetInt32(element, "proxy_queue"),
            Locktime: GetInt32(element, "locktime"));
    }

    private static IReadOnlyList<JsonElement> EnumerateRootObjects(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        using var document = JsonDocument.Parse(json);

        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("The system 'ip' command did not return the expected JSON array.");
        }

        return document.RootElement
            .EnumerateArray()
            .Where(element => element.ValueKind == JsonValueKind.Object)
            .Select(element => element.Clone())
            .ToArray();
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return property.GetString();
    }

    private static int? GetInt32(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        return property.TryGetInt32(out var value) ? value : null;
    }

    private static long? GetInt64(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        return property.TryGetInt64(out var value) ? value : null;
    }

    private static bool GetBoolean(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        return property.ValueKind == JsonValueKind.True ||
               (property.ValueKind == JsonValueKind.False
                   ? false
                   : false);
    }

    private static IReadOnlyList<string> GetStringArray(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        return property
            .EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Cast<string>()
            .ToArray();
    }

    private static IPAddress? ParseOptionalIpAddress(string? value)
    {
        return string.IsNullOrWhiteSpace(value) || !IPAddress.TryParse(value, out var address)
            ? null
            : address;
    }
}
