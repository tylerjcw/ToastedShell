using System.IO;

namespace Tosh.Runtime;

public sealed record FileSystemUsageInfo(
    string FileSystem,
    string MountedOn,
    string? Type,
    StorageSize? Size,
    StorageSize? Used,
    StorageSize? Available,
    int? UsePercent,
    DriveType? DriveType,
    bool IsLocal,
    string? MountRoot = null,
    string? RequestedPath = null,
    bool IsTotal = false,
    long? InodesTotal = null,
    long? InodesUsed = null,
    long? InodesFree = null,
    int? InodeUsePercent = null)
{
    public string MountPoint => MountedOn;

    public string? SourcePath => RequestedPath;
}
