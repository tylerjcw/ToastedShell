using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Tosh.Core;

/// <summary>
/// Cross-platform network information service.
/// On Windows, uses System.Net.NetworkInformation and P/Invoke GetIpForwardTable2.
/// On Linux/macOS, callers should use the 'ip' system utility instead.
/// </summary>
internal static class NetworkInformationServices
{
    // ── interfaces ───────────────────────────────────────────────────

    [SupportedOSPlatform("windows")]
    public static IReadOnlyList<IpInterfaceInfo> GetWindowsInterfaces(bool includeAddresses)
    {
        var nics = NetworkInterface.GetAllNetworkInterfaces();
        var result = new List<IpInterfaceInfo>(nics.Length);

        for (var i = 0; i < nics.Length; i++)
        {
            var nic = nics[i];
            result.Add(ConvertInterface(nic, i, includeAddresses));
        }

        return result;
    }

    [SupportedOSPlatform("windows")]
    private static IpInterfaceInfo ConvertInterface(NetworkInterface nic, int fallbackIndex, bool includeAddresses)
    {
        IPInterfaceProperties? props = null;

        try { props = nic.GetIPProperties(); }
        catch { }

        var adapterIndex = TryGetAdapterIndex(props);
        var mtu = TryGetMtu(props);
        var state = nic.OperationalStatus == OperationalStatus.Up ? "UP" : "DOWN";
        var flags = BuildFlags(nic);
        var mac = nic.GetPhysicalAddress()?.GetAddressBytes();
        var hwAddr = mac is { Length: > 0 } ? FormatMac(mac) : null;
        var addresses = includeAddresses && props is not null
            ? BuildAddresses(props)
            : (IReadOnlyList<IpAddressInfo>)Array.Empty<IpAddressInfo>();

        return new IpInterfaceInfo(
            Index: adapterIndex ?? fallbackIndex,
            Name: nic.Name,
            Flags: flags,
            Mtu: mtu,
            QueueDiscipline: null,
            State: state,
            Group: null,
            QueueLength: null,
            LinkType: MapLinkType(nic.NetworkInterfaceType),
            HardwareAddress: hwAddr,
            BroadcastAddress: null,
            PermanentAddress: null,
            AltNames: Array.Empty<string>(),
            Addresses: addresses);
    }

    private static int? TryGetAdapterIndex(IPInterfaceProperties? props)
    {
        if (props is null) return null;

        try { return props.GetIPv4Properties().Index; }
        catch { }

        try { return props.GetIPv6Properties().Index; }
        catch { }

        return null;
    }

    private static int? TryGetMtu(IPInterfaceProperties? props)
    {
        if (props is null) return null;

        try { return props.GetIPv4Properties().Mtu; }
        catch { }

        return null;
    }

    private static IReadOnlyList<string> BuildFlags(NetworkInterface nic)
    {
        var flags = new List<string>();

        if (nic.OperationalStatus == OperationalStatus.Up) flags.Add("UP");
        if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) flags.Add("LOOPBACK");
        if (nic.SupportsMulticast) flags.Add("MULTICAST");
        if (!nic.IsReceiveOnly) flags.Add("BROADCAST");

        return flags;
    }

    private static string FormatMac(byte[] bytes)
    {
        return string.Join(':', bytes.Select(b => b.ToString("x2")));
    }

    private static string? MapLinkType(NetworkInterfaceType type)
    {
        return type switch
        {
            NetworkInterfaceType.Ethernet => "ether",
            NetworkInterfaceType.Loopback => "loopback",
            NetworkInterfaceType.Wireless80211 => "ether",
            NetworkInterfaceType.TokenRing => "ether",
            NetworkInterfaceType.Fddi => "ether",
            NetworkInterfaceType.Ppp => "ppp",
            NetworkInterfaceType.Tunnel => "tunnel",
            _ => null,
        };
    }

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<IpAddressInfo> BuildAddresses(IPInterfaceProperties props)
    {
        var addresses = new List<IpAddressInfo>();

        foreach (var unicast in props.UnicastAddresses)
        {
            if (unicast.Address.AddressFamily != AddressFamily.InterNetwork &&
                unicast.Address.AddressFamily != AddressFamily.InterNetworkV6)
            {
                continue;
            }

            var family = unicast.Address.AddressFamily == AddressFamily.InterNetwork ? "inet" : "inet6";
            var broadcast = TryComputeBroadcast(unicast);
            var validLifetime = ConvertLifetime(unicast.AddressValidLifetime);
            var preferredLifetime = ConvertLifetime(unicast.AddressPreferredLifetime);

            addresses.Add(new IpAddressInfo(
                Family: family,
                Address: unicast.Address,
                PrefixLength: unicast.PrefixLength,
                Scope: unicast.Address.IsIPv6LinkLocal ? "link" : "global",
                Label: null,
                Broadcast: broadcast,
                Dynamic: unicast.IsDnsEligible,
                NoPrefixRoute: false,
                ValidLifetimeSeconds: validLifetime,
                PreferredLifetimeSeconds: preferredLifetime));
        }

        return addresses;
    }

    private static IPAddress? TryComputeBroadcast(UnicastIPAddressInformation unicast)
    {
        if (unicast.Address.AddressFamily != AddressFamily.InterNetwork) return null;

        try
        {
            var addrBytes = unicast.Address.GetAddressBytes();
            var maskBytes = unicast.IPv4Mask.GetAddressBytes();
            var broadcast = new byte[4];

            for (var i = 0; i < 4; i++)
            {
                broadcast[i] = (byte)(addrBytes[i] | ~maskBytes[i]);
            }

            return new IPAddress(broadcast);
        }
        catch
        {
            return null;
        }
    }

    private static long? ConvertLifetime(long ticks)
    {
        // TimeSpan.MaxValue ticks signal "forever" on Windows
        if (ticks == long.MaxValue || ticks == TimeSpan.MaxValue.Ticks)
        {
            return (long)uint.MaxValue; // ip uses 4294967295 to mean "forever"
        }

        if (ticks <= 0) return null;

        return TimeSpan.FromTicks(ticks).Seconds;
    }

    // ── routes ───────────────────────────────────────────────────────

    [SupportedOSPlatform("windows")]
    public static IReadOnlyList<IpRouteInfo> GetWindowsRoutes()
    {
        IntPtr table = IntPtr.Zero;

        try
        {
            var result = Interop.GetIpForwardTable2(AF_UNSPEC, out table);

            if (result != 0 || table == IntPtr.Zero)
            {
                return Array.Empty<IpRouteInfo>();
            }

            var count = Marshal.ReadInt32(table);

            if (count <= 0)
            {
                return Array.Empty<IpRouteInfo>();
            }

            // MIB_IPFORWARD_TABLE2 layout on 64-bit:
            //   [0..3]  ULONG NumEntries
            //   [4..7]  4 bytes padding (MIB_IPFORWARD_ROW2 has 8-byte alignment due to NET_LUID)
            //   [8..]   MIB_IPFORWARD_ROW2[NumEntries]  (each row = 104 bytes)
            var rowBase = table + 8;

            // Build a name-lookup map from interface index → interface name once.
            var nics = NetworkInterface.GetAllNetworkInterfaces();
            var routes = new List<IpRouteInfo>(count);

            for (var i = 0; i < count; i++)
            {
                var row = rowBase + i * RowSize;
                var route = ParseRoute(row, nics);

                if (route is not null)
                {
                    routes.Add(route);
                }
            }

            return routes;
        }
        finally
        {
            if (table != IntPtr.Zero)
            {
                Interop.FreeMibTable(table);
            }
        }
    }

    // MIB_IPFORWARD_ROW2 field offsets (64-bit Windows):
    //
    //   0   NET_LUID  InterfaceLuid      (8 bytes, align 8)
    //   8   ULONG     InterfaceIndex     (4 bytes)
    //  12   IP_ADDRESS_PREFIX DestinationPrefix
    //            SOCKADDR_INET Prefix    (28 bytes)
    //            UINT8 PrefixLength      (1 byte)
    //            [3 bytes padding]       → total IP_ADDRESS_PREFIX = 32 bytes
    //  44   SOCKADDR_INET NextHop        (28 bytes)
    //  72   UCHAR     SitePrefixLength   (1 byte)
    //  73   [3 bytes padding]
    //  76   ULONG     ValidLifetime      (4 bytes)
    //  80   ULONG     PreferredLifetime  (4 bytes)
    //  84   ULONG     Metric             (4 bytes)
    //  88   UINT      Protocol           (4 bytes)
    //  92   BOOLEAN   Loopback           (1 byte)
    //  93   BOOLEAN   AutoconfigureAddress (1 byte)
    //  94   BOOLEAN   Publish            (1 byte)
    //  95   BOOLEAN   Immortal           (1 byte)
    //  96   ULONG     Age                (4 bytes)
    // 100   UINT      Origin             (4 bytes)
    // Total: 104 bytes

    private const int RowSize = 104;
    private const short AF_INET = 2;
    private const short AF_INET6 = 23;
    private const int AF_UNSPEC = 0;

    private static IpRouteInfo? ParseRoute(IntPtr row, NetworkInterface[] nics)
    {
        // InterfaceIndex at offset 8
        var ifIndex = Marshal.ReadInt32(row + 8);

        // DestinationPrefix.Prefix (SOCKADDR_INET) at offset 12
        var destAddr = ReadSockaddrInet(row + 12);
        var destPrefixLen = Marshal.ReadByte(row + 40); // offset 12 + 28

        // NextHop (SOCKADDR_INET) at offset 44
        var nextHop = ReadSockaddrInet(row + 44);

        // Metric at offset 84
        var metric = (uint)Marshal.ReadInt32(row + 84);

        // Protocol at offset 88
        var protocol = Marshal.ReadInt32(row + 88);

        if (destAddr is null) return null;

        string destination;

        if (IsAllZeros(destAddr) && destPrefixLen == 0)
        {
            destination = "default";
        }
        else
        {
            destination = $"{destAddr}/{destPrefixLen}";
        }

        var deviceName = FindInterfaceName(nics, ifIndex);
        var gateway = nextHop is not null && !IsAllZeros(nextHop) ? nextHop : null;

        return new IpRouteInfo(
            Destination: destination,
            Gateway: gateway,
            Device: deviceName,
            Protocol: MapRouteProtocol(protocol),
            Scope: null,
            PreferredSource: null,
            Metric: metric,
            Preference: null,
            Table: null,
            RouteType: "unicast",
            Flags: Array.Empty<string>());
    }

    private static IPAddress? ReadSockaddrInet(IntPtr ptr)
    {
        // SOCKADDR_INET layout:
        //   [0..1]  SHORT si_family / sin_family / sin6_family
        //   AF_INET  (2): IPv4 address at offset 4, 4 bytes
        //   AF_INET6 (23): IPv6 address at offset 8, 16 bytes; scope_id at offset 24

        var family = Marshal.ReadInt16(ptr);

        if (family == AF_INET)
        {
            var bytes = new byte[4];
            Marshal.Copy(ptr + 4, bytes, 0, 4);
            return new IPAddress(bytes);
        }

        if (family == AF_INET6)
        {
            var bytes = new byte[16];
            Marshal.Copy(ptr + 8, bytes, 0, 16);
            var scopeId = unchecked((uint)Marshal.ReadInt32(ptr + 24));
            return new IPAddress(bytes, scopeId);
        }

        return null;
    }

    private static bool IsAllZeros(IPAddress address)
    {
        return address.GetAddressBytes().All(b => b == 0);
    }

    private static string? FindInterfaceName(NetworkInterface[] nics, int ifIndex)
    {
        foreach (var nic in nics)
        {
            try
            {
                var props = nic.GetIPProperties();

                try
                {
                    if (props.GetIPv4Properties().Index == ifIndex) return nic.Name;
                }
                catch { }

                try
                {
                    if (props.GetIPv6Properties().Index == ifIndex) return nic.Name;
                }
                catch { }
            }
            catch { }
        }

        return null;
    }

    private static string? MapRouteProtocol(int protocol)
    {
        return protocol switch
        {
            2 => "kernel",   // RouteProtocolLocal
            3 => "static",   // RouteProtocolNetMgmt
            19 => "dhcp",    // RouteProtocolDhcp
            10002 => "static", // MIB_IPPROTO_NT_AUTOSTATIC
            10006 => "static", // MIB_IPPROTO_NT_STATIC
            10007 => "static", // MIB_IPPROTO_NT_STATIC_NON_DOD
            _ => null,
        };
    }

    // ── P/Invoke ─────────────────────────────────────────────────────

    private static class Interop
    {
        [DllImport("iphlpapi.dll", SetLastError = true)]
        internal static extern int GetIpForwardTable2(int family, out IntPtr table);

        [DllImport("iphlpapi.dll")]
        internal static extern void FreeMibTable(IntPtr memory);
    }
}
