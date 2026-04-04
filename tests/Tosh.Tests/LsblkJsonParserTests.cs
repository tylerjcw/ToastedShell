using Tosh.Core;

namespace Tosh.Tests;

public sealed class LsblkJsonParserTests
{
    [Fact]
    public void Parser_reads_block_device_trees_and_filesystem_metadata()
    {
        var results = LsblkJsonParser.ParseDevices(
            """
            {
              "blockdevices": [
                {
                  "name": "sda",
                  "kname": "sda",
                  "path": "/dev/sda",
                  "maj:min": "8:0",
                  "maj": "8",
                  "min": "0",
                  "type": "disk",
                  "size": 4096,
                  "model": "Demo Disk",
                  "children": [
                    {
                      "name": "sda1",
                      "kname": "sda1",
                      "pkname": "sda",
                      "path": "/dev/sda1",
                      "maj:min": "8:1",
                      "maj": "8",
                      "min": "1",
                      "type": "part",
                      "size": 1024,
                      "fstype": "ext4",
                      "fsver": "1.0",
                      "fsavail": 512,
                      "fssize": 1024,
                      "fsused": 512,
                      "fsuse%": "50%",
                      "mountpoint": "/",
                      "mountpoints": ["/"],
                      "fsroots": ["/"]
                    }
                  ]
                }
              ]
            }
            """);

        var disk = Assert.Single(results);
        Assert.Equal("sda", disk.Name);
        Assert.Equal("/dev/sda", disk.Path);
        Assert.Equal("8:0", disk.MajorMinor);
        Assert.Equal(StorageSize.FromBytes(4_096), disk.Size);
        Assert.Equal("Demo Disk", disk.Model);

        var partition = Assert.Single(disk.Children);
        Assert.Equal("sda1", partition.Name);
        Assert.Equal("sda", partition.ParentKernelName);
        Assert.Equal("ext4", partition.FileSystemType);
        Assert.Equal("1.0", partition.FileSystemVersion);
        Assert.Equal(50, partition.FileSystemUsePercent);
        Assert.Equal("/", partition.MountPoint);
        Assert.Equal(["/"], partition.MountPoints);
        Assert.Equal(StorageSize.FromBytes(512), partition.FileSystemAvailable);
    }
}
