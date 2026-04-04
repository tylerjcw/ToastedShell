namespace Tosh.Core;

public sealed record class MountInfo : IDisplayTreeNode
{
    public string Target { get; init; } = string.Empty;

    public string? Source { get; init; }

    public IReadOnlyList<string> Sources { get; init; } = Array.Empty<string>();

    public string? FileSystemType { get; init; }

    public string? FileSystemRoot { get; init; }

    public string? Options { get; init; }

    public string? FileSystemOptions { get; init; }

    public string? VfsOptions { get; init; }

    public string? OptionalFields { get; init; }

    public string? Propagation { get; init; }

    public string? Label { get; init; }

    public string? Uuid { get; init; }

    public string? PartitionLabel { get; init; }

    public string? PartitionUuid { get; init; }

    public string? MajorMinor { get; init; }

    public StorageSize? Size { get; init; }

    public StorageSize? Used { get; init; }

    public StorageSize? Available { get; init; }

    public int? UsePercent { get; init; }

    public long? InodesAvailable { get; init; }

    public long? InodesTotal { get; init; }

    public long? InodesUsed { get; init; }

    public int? InodeUsePercent { get; init; }

    public int? Id { get; init; }

    public int? ParentId { get; init; }

    public int? TaskId { get; init; }

    public long? UniqueId { get; init; }

    public int? FrequencyDays { get; init; }

    public int? PassNumber { get; init; }

    public IReadOnlyList<MountInfo> Children { get; init; } = Array.Empty<MountInfo>();

    internal bool PreferByteSizes { get; init; }

    public object? DisplaySize => RenderSizedValue(Size);

    public object? DisplayUsed => RenderSizedValue(Used);

    public object? DisplayAvailable => RenderSizedValue(Available);

    public string UseText => UsePercent is int percent ? $"{percent}%" : string.Empty;

    public string InodeUseText => InodeUsePercent is int percent ? $"{percent}%" : string.Empty;

    public string SourcesText => Sources.Count == 0 ? string.Empty : string.Join(Environment.NewLine, Sources);

    IEnumerable<object> IDisplayTreeNode.GetDisplayChildren() => Children.Cast<object>();

    public MountInfo WithDisplayPreferences(bool preferByteSizes)
    {
        return this with
        {
            PreferByteSizes = preferByteSizes,
            Children = Children.Select(child => child.WithDisplayPreferences(preferByteSizes)).ToArray(),
        };
    }

    public override string ToString()
    {
        return string.IsNullOrWhiteSpace(Source)
            ? Target
            : $"{Target} <- {Source}";
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
