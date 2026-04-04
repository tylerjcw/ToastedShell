using Tosh.Core;

namespace Tosh.Tests;

public sealed class LscpuJsonParserTests
{
    [Fact]
    public void Summary_parser_reads_flattened_cpu_metadata()
    {
        var result = LscpuJsonParser.ParseSummary(
            """
            {
              "lscpu": [
                { "field": "Architecture:", "data": "x86_64" },
                { "field": "CPU(s):", "data": "32" },
                { "field": "On-line CPU(s) list:", "data": "0-31" },
                { "field": "Vendor ID:", "data": "AuthenticAMD" },
                { "field": "Model name:", "data": "Demo CPU" },
                { "field": "Thread(s) per core:", "data": "2" },
                { "field": "Core(s) per socket:", "data": "16" },
                { "field": "Socket(s):", "data": "1" },
                { "field": "Address sizes:", "data": "48 bits physical, 48 bits virtual" },
                { "field": "Flags:", "data": "fpu sse avx" },
                { "field": "NUMA node(s):", "data": "1" },
                { "field": "NUMA node0 CPU(s):", "data": "0-31" },
                { "field": "Vulnerability Spectre v2:", "data": "Mitigated" }
              ]
            }
            """);

        Assert.Equal("x86_64", result.Architecture);
        Assert.Equal(32, result.CpuCount);
        Assert.Equal("0-31", result.OnlineCpuList);
        Assert.Equal("AuthenticAMD", result.VendorId);
        Assert.Equal("Demo CPU", result.ModelName);
        Assert.Equal(48, result.PhysicalAddressBits);
        Assert.Equal(48, result.VirtualAddressBits);
        Assert.Equal(["fpu", "sse", "avx"], result.Flags);
        Assert.Equal("0-31", result.NumaNodes["NUMA node0 CPU(s)"]);
        Assert.Equal("Mitigated", result.Vulnerabilities["Spectre v2"]);
    }

    [Fact]
    public void Summary_parser_flattens_hierarchic_summary_output()
    {
        var result = LscpuJsonParser.ParseSummary(
            """
            {
              "lscpu": [
                {
                  "field": "Vendor ID:",
                  "data": "AuthenticAMD",
                  "children": [
                    {
                      "field": "Model name:",
                      "data": "Demo CPU",
                      "children": [
                        { "field": "CPU family:", "data": "26" }
                      ]
                    }
                  ]
                }
              ]
            }
            """);

        Assert.Equal("AuthenticAMD", result.VendorId);
        Assert.Equal("Demo CPU", result.ModelName);
        Assert.Equal(26, result.CpuFamily);
    }

    [Fact]
    public void Topology_parser_reads_extended_rows()
    {
        var results = LscpuJsonParser.ParseTopology(
            """
            {
              "cpus": [
                {
                  "cpu": 0,
                  "node": 0,
                  "socket": 0,
                  "core": 0,
                  "online": true,
                  "mhz": 5000.5,
                  "maxmhz": 5750.0,
                  "minmhz": 600.0,
                  "modelname": "Demo CPU"
                }
              ]
            }
            """);

        var cpu = Assert.Single(results);
        Assert.Equal(0, cpu.Cpu);
        Assert.Equal(0, cpu.Node);
        Assert.True(cpu.Online);
        Assert.Equal(5000.5, cpu.Mhz);
        Assert.Equal("Demo CPU", cpu.ModelName);
    }

    [Fact]
    public void Cache_parser_reads_size_and_policy_metadata()
    {
        var results = LscpuJsonParser.ParseCaches(
            """
            {
              "caches": [
                {
                  "name": "L1d",
                  "one-size": "48K",
                  "all-size": "768K",
                  "ways": 12,
                  "type": "Data",
                  "level": 1,
                  "alloc-policy": "write-allocate",
                  "write-policy": "write-back",
                  "sets": 64,
                  "phy-line": 1,
                  "coherency-size": 64
                }
              ]
            }
            """,
            preferByteSizes: false);

        var cache = Assert.Single(results);
        Assert.Equal("L1d", cache.Name);
        Assert.Equal(StorageSize.FromBytes(48_000), cache.OneSize);
        Assert.Equal(StorageSize.FromBytes(768_000), cache.AllSize);
        Assert.Equal("write-allocate", cache.AllocationPolicy);
        Assert.Equal("write-back", cache.WritePolicy);
    }
}
