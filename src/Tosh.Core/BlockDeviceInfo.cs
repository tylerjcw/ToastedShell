namespace Tosh.Core;

public sealed record class BlockDeviceInfo : IDisplayTreeNode
{
    public string Name { get; init; } = string.Empty;

    public string? Path { get; init; }

    public string? KernelName { get; init; }

    public string? ParentKernelName { get; init; }

    public string? Type { get; init; }

    public string? MajorMinor { get; init; }

    public int? Major { get; init; }

    public int? Minor { get; init; }

    public StorageSize Size { get; init; }

    public long ByteSize => Size.Bytes;

    public StorageSize? FileSystemSize { get; init; }

    public StorageSize? FileSystemUsed { get; init; }

    public StorageSize? FileSystemAvailable { get; init; }

    public int? FileSystemUsePercent { get; init; }

    public string? FileSystemType { get; init; }

    public string? FileSystemVersion { get; init; }

    public string? Label { get; init; }

    public string? Uuid { get; init; }

    public string? PartitionLabel { get; init; }

    public string? PartitionUuid { get; init; }

    public string? PartitionType { get; init; }

    public string? PartitionTypeName { get; init; }

    public int? PartitionNumber { get; init; }

    public string? PartitionTableType { get; init; }

    public string? PartitionTableUuid { get; init; }

    public string? Model { get; init; }

    public string? Serial { get; init; }

    public string? Vendor { get; init; }

    public string? Transport { get; init; }

    public string? State { get; init; }

    public string? Owner { get; init; }

    public string? Group { get; init; }

    public string? Mode { get; init; }

    public string? Hctl { get; init; }

    public string? Scheduler { get; init; }

    public string? Subsystems { get; init; }

    public bool ReadOnly { get; init; }

    public bool Removable { get; init; }

    public bool HotPlug { get; init; }

    public bool Rotational { get; init; }

    public bool Random { get; init; }

    public bool Dax { get; init; }

    public bool DiscardZero { get; init; }

    public int? Alignment { get; init; }

    public StorageSize? DiscardAlignment { get; init; }

    public StorageSize? DiscardGranularity { get; init; }

    public StorageSize? DiscardMax { get; init; }

    public int? DiskSequence { get; init; }

    public int? LogicalSectorSize { get; init; }

    public int? PhysicalSectorSize { get; init; }

    public int? MinimumIoSize { get; init; }

    public int? OptimalIoSize { get; init; }

    public int? RequestQueueSize { get; init; }

    public int? ReadAhead { get; init; }

    public long? Start { get; init; }

    public StorageSize? WSame { get; init; }

    public string? Zoned { get; init; }

    public StorageSize? ZoneSize { get; init; }

    public StorageSize? ZoneWriteGranularity { get; init; }

    public StorageSize? ZoneAppendSize { get; init; }

    public int? ZoneCount { get; init; }

    public int? ZoneOpenMax { get; init; }

    public int? ZoneActiveMax { get; init; }

    public string? MountPoint { get; init; }

    public IReadOnlyList<string> MountPoints { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> FileSystemRoots { get; init; } = Array.Empty<string>();

    public IReadOnlyList<BlockDeviceInfo> Children { get; init; } = Array.Empty<BlockDeviceInfo>();

    internal bool PreferByteSizes { get; init; }

    public string MountPointsText => MountPoints.Count == 0 ? string.Empty : string.Join(Environment.NewLine, MountPoints);

    public string FileSystemRootsText => FileSystemRoots.Count == 0 ? string.Empty : string.Join(Environment.NewLine, FileSystemRoots);

    public string FileSystemUseText => FileSystemUsePercent is int percent ? $"{percent}%" : string.Empty;

    public object DisplaySize => PreferByteSizes ? ByteSize : Size;

    public object? DisplayFileSystemSize => RenderSizedValue(FileSystemSize);

    public object? DisplayFileSystemUsed => RenderSizedValue(FileSystemUsed);

    public object? DisplayFileSystemAvailable => RenderSizedValue(FileSystemAvailable);

    public object? DisplayDiscardAlignment => RenderSizedValue(DiscardAlignment);

    public object? DisplayDiscardGranularity => RenderSizedValue(DiscardGranularity);

    public object? DisplayDiscardMax => RenderSizedValue(DiscardMax);

    public object? DisplayWSame => RenderSizedValue(WSame);

    public object? DisplayZoneSize => RenderSizedValue(ZoneSize);

    public object? DisplayZoneWriteGranularity => RenderSizedValue(ZoneWriteGranularity);

    public object? DisplayZoneAppendSize => RenderSizedValue(ZoneAppendSize);

    IEnumerable<object> IDisplayTreeNode.GetDisplayChildren() => Children.Cast<object>();

    public BlockDeviceInfo WithDisplayPreferences(bool preferByteSizes)
    {
        return this with
        {
            PreferByteSizes = preferByteSizes,
            Children = Children.Select(child => child.WithDisplayPreferences(preferByteSizes)).ToArray(),
        };
    }

    public override string ToString()
    {
        return string.IsNullOrWhiteSpace(Type)
            ? Name
            : $"{Name} ({Type})";
    }

    private object? RenderSizedValue(StorageSize? value)
    {
        if (value is not StorageSize size)
        {
            return null;
        }

        return PreferByteSizes ? size.Bytes : size;
    }
}
