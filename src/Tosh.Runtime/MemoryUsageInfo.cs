namespace Tosh.Runtime;

public sealed record MemoryUsageInfo(
    string Category,
    StorageSize? Total,
    StorageSize? Used,
    StorageSize? Free,
    StorageSize? Shared,
    StorageSize? BuffCache,
    StorageSize? Available);
