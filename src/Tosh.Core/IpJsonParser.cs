using System.Net;
using System.Text.Json;

namespace Tosh.Core;

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
