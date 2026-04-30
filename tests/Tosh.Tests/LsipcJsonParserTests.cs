using System.Dynamic;
using Tosh.Runtime;

namespace Tosh.Tests;

public sealed class LsipcJsonParserTests
{
    [Fact]
    public void Parser_reads_ipc_rows_with_typed_values()
    {
        var results = LsipcJsonParser.ParseRows(
            """
            {
              "sharedmemory": [
                {
                  "key": "0x00000000",
                  "id": "6",
                  "perms": "rw-------",
                  "owner": "komrad",
                  "size": "2M",
                  "nattch": "2",
                  "status": "dest",
                  "ctime": "2026-03-27T23:10:05-04:00",
                  "cpid": "2144",
                  "lpid": "1172",
                  "attach": "2026-03-27T23:10:05-04:00",
                  "detach": null,
                  "command": "/usr/bin/dunst"
                }
              ]
            }
            """);

        var row = Assert.IsAssignableFrom<IDictionary<string, object?>>(Assert.Single(results));
        Assert.Equal("0x00000000", row["Key"]);
        Assert.Equal(6L, row["Id"]);
        Assert.Equal(StorageSize.FromBytes(2_000_000), row["Size"]);
        Assert.Equal(2L, row["AttachCount"]);
        Assert.Equal("/usr/bin/dunst", row["Command"]);
        Assert.IsType<DateTimeOffset>(row["Changed"]);
        Assert.IsType<DateTimeOffset>(row["Attached"]);
        Assert.Null(row["Detached"]);
    }

    [Fact]
    public void Parser_reads_global_limit_rows()
    {
        var results = LsipcJsonParser.ParseRows(
            """
            {
              "ipclimits": [
                {
                  "resource": "SHMMAX",
                  "description": "Max size of shared memory segment (bytes)",
                  "limit": "8192",
                  "used": "-",
                  "use%": "-"
                }
              ]
            }
            """);

        var row = Assert.IsAssignableFrom<IDictionary<string, object?>>(Assert.Single(results));
        Assert.Equal("SHMMAX", row["Resource"]);
        Assert.Equal("Max size of shared memory segment (bytes)", row["Description"]);
        Assert.Equal(8192L, row["Limit"]);
        Assert.Equal("-", row["Used"]);
        Assert.Equal("-", row["UsePercent"]);
    }
}
