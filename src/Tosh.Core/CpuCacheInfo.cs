namespace Tosh.Core;

public sealed record class CpuCacheInfo
{
    public string? Name { get; init; }

    public int? Level { get; init; }

    public string? Type { get; init; }

    public StorageSize? OneSize { get; init; }

    public string? OneSizeText { get; init; }

    public StorageSize? AllSize { get; init; }

    public string? AllSizeText { get; init; }

    public int? Ways { get; init; }

    public string? AllocationPolicy { get; init; }

    public string? WritePolicy { get; init; }

    public int? PhysicalLineCount { get; init; }

    public int? Sets { get; init; }

    public int? CoherencySize { get; init; }

    internal bool PreferByteSizes { get; init; }

    public object? DisplayOneSize => RenderSize(OneSize, OneSizeText);

    public object? DisplayAllSize => RenderSize(AllSize, AllSizeText);

    public CpuCacheInfo WithDisplayPreferences(bool preferByteSizes) => this with { PreferByteSizes = preferByteSizes };

    public override string ToString()
    {
        return string.IsNullOrWhiteSpace(Name)
            ? "CPU cache"
            : Name!;
    }

    private object? RenderSize(StorageSize? size, string? fallback)
    {
        if (size is StorageSize resolved)
        {
            return PreferByteSizes ? resolved.Bytes : resolved;
        }

        return fallback;
    }
}
