namespace Tosh.Core;

public static class PathUtilities
{
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static string UserHomeDirectory =>
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static string ResolvePath(string currentDirectory, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currentDirectory);

        if (string.IsNullOrWhiteSpace(path))
        {
            return currentDirectory;
        }

        var expandedPath = ExpandHomeDirectory(path);

        return Path.IsPathRooted(expandedPath)
            ? Path.GetFullPath(expandedPath)
            : Path.GetFullPath(expandedPath, currentDirectory);
    }

    public static StringComparison GetPathComparison() => PathComparison;

    private static string ExpandHomeDirectory(string path)
    {
        if (path == "~")
        {
            return UserHomeDirectory;
        }

        if (path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
        {
            return Path.Combine(UserHomeDirectory, path[2..]);
        }

        return path;
    }
}
