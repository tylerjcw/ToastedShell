namespace Tosh.Core;

public sealed record PathUsageInfo(
    string Name,
    string FullName,
    int Depth,
    bool IsDirectory,
    StorageSize Size,
    DateTimeOffset? Modified = null,
    bool IsTotal = false)
{
    public string Path => FullName;

    public string Type => IsTotal ? "total" : IsDirectory ? "dir" : "file";
}
