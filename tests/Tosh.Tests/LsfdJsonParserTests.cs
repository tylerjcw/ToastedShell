using Tosh.Core;

namespace Tosh.Tests;

public sealed class LsfdJsonParserTests
{
    [Fact]
    public void Parser_reads_descriptor_rows_and_summary_rows()
    {
        var result = LsfdJsonParser.Parse(
            """
            {
              "lsfd": [
                {
                  "command": "bash",
                  "pid": 123,
                  "user": "komrad",
                  "assoc": "cwd",
                  "fd": 3,
                  "xmode": "rw----",
                  "type": "REG",
                  "source": "/",
                  "mntid": 42,
                  "inode": 99,
                  "name": "/tmp/demo.txt",
                  "size": 4096,
                  "sock.type": "stream"
                }
              ]
            }
            {
              "lsfd-summary": [
                { "counter": "open files", "value": 10 }
              ]
            }
            """);

        var row = Assert.Single(result.Rows);
        Assert.Equal("bash", row.Command);
        Assert.Equal(123, row.ProcessId);
        Assert.Equal(3, row.FileDescriptor);
        Assert.Equal("REG", row.Type);
        Assert.Equal(StorageSize.FromBytes(4096), row.Size);
        Assert.Equal("stream", row.SocketType);

        var summary = Assert.Single(result.Summary);
        Assert.Equal("open files", summary.Counter);
        Assert.Equal(10, summary.Value);
    }
}
