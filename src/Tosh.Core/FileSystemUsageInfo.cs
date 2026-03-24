using System.IO;

namespace Tosh.Core;

public sealed record FileSystemUsageInfo(
    string FileSystem,
    string MountedOn,
    string? Type,
    StorageSize? Size,
    StorageSize? Used,
    StorageSize? Available,
    int? UsePercent,
    DriveType? DriveType)
{
    public string MountPoint => MountedOn;
}
