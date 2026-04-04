using Tosh.Core;

namespace Tosh.Tests;

public sealed class FindmntJsonParserTests
{
    [Fact]
    public void Parser_reads_mount_trees_and_size_metadata()
    {
        var results = FindmntJsonParser.ParseMounts(
            """
            {
              "filesystems": [
                {
                  "target": "/",
                  "source": "/dev/root",
                  "fstype": "ext4",
                  "fsroot": "/",
                  "options": "rw,relatime",
                  "fs-options": "rw",
                  "vfs-options": "rw,relatime",
                  "id": 10,
                  "parent": 1,
                  "size": 1024,
                  "used": 256,
                  "avail": 768,
                  "use%": "25%",
                  "ino.total": "100",
                  "ino.used": "5",
                  "ino.avail": "95",
                  "ino.use%": "5%",
                  "sources": ["/dev/root"],
                  "children": [
                    {
                      "target": "/tmp",
                      "source": "tmpfs",
                      "fstype": "tmpfs",
                      "size": 512,
                      "used": 12,
                      "avail": 500,
                      "use%": "2%"
                    }
                  ]
                }
              ]
            }
            """);

        var root = Assert.Single(results);
        Assert.Equal("/", root.Target);
        Assert.Equal("/dev/root", root.Source);
        Assert.Equal("ext4", root.FileSystemType);
        Assert.Equal(StorageSize.FromBytes(1024), root.Size);
        Assert.Equal(25, root.UsePercent);
        Assert.Equal(100, root.InodesTotal);
        Assert.Equal(["/dev/root"], root.Sources);

        var child = Assert.Single(root.Children);
        Assert.Equal("/tmp", child.Target);
        Assert.Equal("tmpfs", child.FileSystemType);
        Assert.Equal(StorageSize.FromBytes(500), child.Available);
    }
}
