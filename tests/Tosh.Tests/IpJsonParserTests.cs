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

  [Fact]
  public void Parser_projects_ip_tunnel_json_into_typed_tunnel_objects()
  {
    var results = IpJsonParser.ParseTunnels(
        """
            [
              {
                "ifname": "gre0",
                "mode": "gre",
                "remote": "10.0.0.1",
                "local": "10.0.0.2",
                "ttl": 64,
                "pmtudisc": true,
                "dev": "eth0"
              }
            ]
            """);

    var tunnel = Assert.Single(results);
    Assert.Equal("gre0", tunnel.Name);
    Assert.Equal("gre", tunnel.Mode);
    Assert.Equal("10.0.0.1", tunnel.Remote);
    Assert.Equal("10.0.0.2", tunnel.Local);
    Assert.Equal(64, tunnel.Ttl);
    Assert.True(tunnel.Pmtudisc);
    Assert.Equal("eth0", tunnel.Dev);
  }

  [Fact]
  public void Parser_projects_ip_tuntap_json_into_typed_tuntap_objects()
  {
    var results = IpJsonParser.ParseTuntaps(
        """
            [
              {
                "ifname": "tun0",
                "mode": "tun",
                "user": "nobody",
                "group": "nogroup",
                "multi_queue": true,
                "flags": ["UP", "RUNNING"]
              }
            ]
            """);

    var tuntap = Assert.Single(results);
    Assert.Equal("tun0", tuntap.Name);
    Assert.Equal("tun", tuntap.Mode);
    Assert.Equal("nobody", tuntap.User);
    Assert.Equal("nogroup", tuntap.Group);
    Assert.True(tuntap.MultiQueue);
    Assert.Equal(["UP", "RUNNING"], tuntap.Flags);
  }

  [Fact]
  public void Parser_projects_ip_vrf_json_into_typed_vrf_objects()
  {
    var results = IpJsonParser.ParseVrfs(
        """
            [
              {
                "name": "mgmt",
                "table": 100
              }
            ]
            """);

    var vrf = Assert.Single(results);
    Assert.Equal("mgmt", vrf.Name);
    Assert.Equal(100, vrf.TableId);
  }

  [Fact]
  public void Parser_projects_ip_maddr_json_into_typed_maddr_objects()
  {
    var results = IpJsonParser.ParseMaddrs(
        """
            [
              {
                "ifindex": 1,
                "ifname": "lo",
                "maddr": [
                  {"family": "inet", "address": "224.0.0.1"},
                  {"family": "inet6", "address": "ff02::1"},
                  {"link": "01:00:5e:00:00:01"}
                ]
              }
            ]
            """);

    var maddr = Assert.Single(results);
    Assert.Equal(1, maddr.Index);
    Assert.Equal("lo", maddr.Name);
    Assert.Equal(3, maddr.AddressCount);

    Assert.Collection(
        maddr.Addresses,
        entry =>
        {
          Assert.Equal("inet", entry.Family);
          Assert.Equal("224.0.0.1", entry.Address);
          Assert.Null(entry.Link);
        },
        entry =>
        {
          Assert.Equal("inet6", entry.Family);
          Assert.Equal("ff02::1", entry.Address);
        },
        entry =>
        {
          Assert.Null(entry.Family);
          Assert.Equal("01:00:5e:00:00:01", entry.Link);
          Assert.Null(entry.Address);
        });
  }

  [Fact]
  public void Parser_projects_ip_mroute_json_into_typed_mroute_objects()
  {
    var results = IpJsonParser.ParseMroutes(
        """
            [
              {
                "group": "239.1.1.1",
                "src": "10.0.0.1",
                "iif": "eth0",
                "oifs": [{"dev": "eth1"}, {"dev": "eth2"}],
                "packets": 42,
                "bytes": 12345
              }
            ]
            """);

    var route = Assert.Single(results);
    Assert.Equal("239.1.1.1", route.Group);
    Assert.Equal("10.0.0.1", route.Source);
    Assert.Equal("eth0", route.Iif);
    Assert.Equal(["eth1", "eth2"], route.Oifs);
    Assert.Equal(42, route.Packets);
    Assert.Equal(12345, route.Bytes);
  }

  [Fact]
  public void Parser_projects_ip_token_json_into_typed_token_objects()
  {
    var results = IpJsonParser.ParseTokens(
        """
            [
              {"token": "::", "ifname": "eno1"},
              {"token": "::1", "ifname": "wlan0"}
            ]
            """);

    Assert.Equal(2, results.Count);
    Assert.Equal("::", results[0].Token);
    Assert.Equal("eno1", results[0].InterfaceName);
    Assert.Equal("::1", results[1].Token);
    Assert.Equal("wlan0", results[1].InterfaceName);
  }

  [Fact]
  public void Parser_projects_ip_ntable_json_into_typed_ntable_objects()
  {
    var results = IpJsonParser.ParseNtables(
        """
            [
              {
                "family": "inet",
                "name": "arp_cache",
                "dev": "eno1",
                "thresh1": 128,
                "thresh2": 512,
                "thresh3": 1024,
                "gc_interval": 30000,
                "refcnt": 9,
                "reachable": 25357,
                "base_reachable": 30000,
                "retrans": 1000,
                "gc_stale": 60000,
                "delay_probe": 5000,
                "queue": 101,
                "app_probes": 0,
                "ucast_probes": 3,
                "mcast_probes": 3,
                "mcast_reprobes": 0,
                "anycast_delay": 1000,
                "proxy_delay": 800,
                "proxy_queue": 64,
                "locktime": 1000
              }
            ]
            """);

    var ntable = Assert.Single(results);
    Assert.Equal("inet", ntable.Family);
    Assert.Equal("arp_cache", ntable.Name);
    Assert.Equal("eno1", ntable.Dev);
    Assert.Equal(128, ntable.Thresh1);
    Assert.Equal(512, ntable.Thresh2);
    Assert.Equal(1024, ntable.Thresh3);
    Assert.Equal(30000, ntable.GcInterval);
    Assert.Equal(9, ntable.RefCount);
    Assert.Equal(25357, ntable.Reachable);
    Assert.Equal(30000, ntable.BaseReachable);
    Assert.Equal(1000, ntable.Retrans);
    Assert.Equal(60000, ntable.GcStale);
    Assert.Equal(3, ntable.UnicastProbes);
    Assert.Equal(3, ntable.MulticastProbes);
    Assert.Equal(0, ntable.MulticastReprobes);
    Assert.Equal(1000, ntable.Locktime);
  }

  [Fact]
  public void Parser_returns_empty_for_null_or_whitespace_json()
  {
    Assert.Empty(IpJsonParser.ParseTunnels(""));
    Assert.Empty(IpJsonParser.ParseTuntaps(""));
    Assert.Empty(IpJsonParser.ParseVrfs(""));
    Assert.Empty(IpJsonParser.ParseMaddrs(""));
    Assert.Empty(IpJsonParser.ParseMroutes(""));
    Assert.Empty(IpJsonParser.ParseTokens(""));
    Assert.Empty(IpJsonParser.ParseNtables(""));
    Assert.Empty(IpJsonParser.ParseNamespaces(""));
  }
}
