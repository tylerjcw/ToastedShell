namespace Tosh.Core;

public sealed record PathUsageInfo(
    string Name,
    string FullName,
    int Depth,
    bool IsDirectory,
    StorageSize Size)
{
    public string Path => FullName;

    public string Type => IsDirectory ? "dir" : "file";
}
