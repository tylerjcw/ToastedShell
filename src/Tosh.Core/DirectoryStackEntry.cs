namespace Tosh.Core;

public sealed record DirectoryStackEntry(int Index, string Path, bool IsCurrent)
{
    public string Name => System.IO.Path.GetFileName(Path) is { Length: > 0 } name ? name : Path;

    public FileSystemEntry ToFileSystemEntry() =>
        FileSystemEntry.From(new DirectoryInfo(Path));

    public override string ToString() => IsCurrent ? $"* {Path}" : $"  {Path}";
}
