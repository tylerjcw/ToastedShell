namespace Tosh.Core;

public sealed record IpInterfaceInfo(
    int Index,
    string Name,
    IReadOnlyList<string> Flags,
    int? Mtu,
    string? QueueDiscipline,
    string? State,
    string? Group,
    int? QueueLength,
    string? LinkType,
    string? HardwareAddress,
    string? BroadcastAddress,
    string? PermanentAddress,
    IReadOnlyList<string> AltNames,
    IReadOnlyList<IpAddressInfo> Addresses)
{
    public bool IsUp =>
        string.Equals(State, "UP", StringComparison.OrdinalIgnoreCase) ||
        Flags.Any(flag => string.Equals(flag, "UP", StringComparison.OrdinalIgnoreCase));

    public string? IPv4 => Addresses.FirstOrDefault(address => address.IsIpv4)?.Cidr;

    public string? IPv6 => Addresses.FirstOrDefault(address => address.IsIpv6)?.Cidr;

    public int AddressCount => Addresses.Count;

    public string FlagsText => string.Join(", ", Flags);

    public string AltNamesText => string.Join(", ", AltNames);
}
