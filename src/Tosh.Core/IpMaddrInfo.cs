namespace Tosh.Core;

public sealed record IpMaddrInfo(
    int Index,
    string Name,
    IReadOnlyList<IpMaddrEntry> Addresses)
{
    public int AddressCount => Addresses.Count;
}
