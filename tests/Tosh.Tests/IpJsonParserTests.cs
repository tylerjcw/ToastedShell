using System.Net;
using Tosh.Core;

namespace Tosh.Tests;

public sealed class IpJsonParserTests
{
    [Fact]
    public void Parser_projects_ip_addr_json_into_typed_interface_objects()
    {
        var results = IpJsonParser.ParseInterfaces(
            """
            [
              {
                "ifindex": 2,
                "ifname": "eno1",
                "flags": ["BROADCAST", "MULTICAST", "UP", "LOWER_UP"],
                "mtu": 1500,
                "qdisc": "fq_codel",
                "operstate": "UP",
                "group": "default",
                "txqlen": 1000,
                "link_type": "ether",
                "address": "60:cf:84:ca:2b:77",
                "broadcast": "ff:ff:ff:ff:ff:ff",
                "altnames": ["enp107s0", "enx60cf84ca2b77"],
                "addr_info": [
                  {
                    "family": "inet",
                    "local": "192.168.254.16",
                    "prefixlen": 24,
                    "broadcast": "192.168.254.255",
                    "scope": "global",
                    "dynamic": true,
                    "noprefixroute": true,
                    "label": "eno1",
                    "valid_life_time": 55591,
                    "preferred_life_time": 55591
                  },
                  {
                    "family": "inet6",
                    "local": "fe80::8755:24b7:d53e:5087",
                    "prefixlen": 64,
                    "scope": "link",
                    "noprefixroute": true,
                    "valid_life_time": 4294967295,
                    "preferred_life_time": 4294967295
                  }
                ]
              }
            ]
            """);

        var networkInterface = Assert.Single(results);
        Assert.Equal(2, networkInterface.Index);
        Assert.Equal("eno1", networkInterface.Name);
        Assert.Equal("UP", networkInterface.State);
        Assert.Equal("192.168.254.16/24", networkInterface.IPv4);
        Assert.Equal("fe80::8755:24b7:d53e:5087/64", networkInterface.IPv6);
        Assert.Equal(["enp107s0", "enx60cf84ca2b77"], networkInterface.AltNames);

        Assert.Collection(
            networkInterface.Addresses,
            address =>
            {
                Assert.Equal("inet", address.Family);
                Assert.Equal(IPAddress.Parse("192.168.254.16"), address.Address);
                Assert.Equal("192.168.254.16/24", address.Cidr);
                Assert.Equal("55591 sec", address.ValidLifetime);
                Assert.True(address.Dynamic);
            },
            address =>
            {
                Assert.Equal("inet6", address.Family);
                Assert.Equal(IPAddress.Parse("fe80::8755:24b7:d53e:5087"), address.Address);
                Assert.Equal("forever", address.ValidLifetime);
                Assert.True(address.NoPrefixRoute);
            });
    }
}
