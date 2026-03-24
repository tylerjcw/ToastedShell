namespace Tosh.Core;

public static class ExternalCommandResolver
{
    public static ExternalCommandLookupResult Resolve(string currentDirectory, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currentDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return LooksLikeExplicitPath(name)
            ? ResolveExplicitPath(currentDirectory, name)
            : ResolveFromPath(currentDirectory, name);
    }

    public static IReadOnlyList<string> FindAllExecutables(string currentDirectory, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currentDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var matches = new List<string>();

        foreach (var candidate in EnumerateCandidates(currentDirectory, name))
        {
            if (!seen.Add(candidate))
            {
                continue;
            }

            if (File.Exists(candidate) && IsExecutable(candidate))
            {
                matches.Add(candidate);
            }
        }

        return matches;
    }

    private static ExternalCommandLookupResult ResolveExplicitPath(string currentDirectory, string name)
    {
        var resolvedPath = PathUtilities.ResolvePath(currentDirectory, name);

        if (Directory.Exists(resolvedPath))
        {
            return new ExternalCommandLookupResult(name, ExternalCommandLookupStatus.IsDirectory, resolvedPath, IsExplicitPath: true);
        }

        if (!File.Exists(resolvedPath))
        {
            return new ExternalCommandLookupResult(name, ExternalCommandLookupStatus.NotFound, resolvedPath, IsExplicitPath: true);
        }

        return IsExecutable(resolvedPath)
            ? new ExternalCommandLookupResult(name, ExternalCommandLookupStatus.Found, resolvedPath, IsExplicitPath: true)
            : new ExternalCommandLookupResult(name, ExternalCommandLookupStatus.NotExecutable, resolvedPath, IsExplicitPath: true);
    }

    private static ExternalCommandLookupResult ResolveFromPath(string currentDirectory, string name)
    {
        string? firstNonExecutable = null;
        string? firstDirectory = null;
        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        foreach (var candidate in EnumerateCandidates(currentDirectory, name))
        {
            if (!seen.Add(candidate))
            {
                continue;
            }

            if (Directory.Exists(candidate))
            {
                firstDirectory ??= candidate;
                continue;
            }

            if (!File.Exists(candidate))
            {
                continue;
            }

            if (IsExecutable(candidate))
            {
                return new ExternalCommandLookupResult(name, ExternalCommandLookupStatus.Found, candidate, IsExplicitPath: false);
            }

            firstNonExecutable ??= candidate;
        }

        if (firstNonExecutable is not null)
        {
            return new ExternalCommandLookupResult(name, ExternalCommandLookupStatus.NotExecutable, firstNonExecutable, IsExplicitPath: false);
        }

        if (firstDirectory is not null)
        {
            return new ExternalCommandLookupResult(name, ExternalCommandLookupStatus.IsDirectory, firstDirectory, IsExplicitPath: false);
        }

        return new ExternalCommandLookupResult(name, ExternalCommandLookupStatus.NotFound, null, IsExplicitPath: false);
    }

    private static IEnumerable<string> EnumerateCandidates(string currentDirectory, string name)
    {
        if (LooksLikeExplicitPath(name))
        {
            yield return PathUtilities.ResolvePath(currentDirectory, name);
            yield break;
        }

        var pathValue = Environment.GetEnvironmentVariable("PATH");

        if (string.IsNullOrWhiteSpace(pathValue))
        {
            yield break;
        }

        var extensions = GetExecutableExtensions();

        foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var candidate in BuildCandidates(directory, name, extensions))
            {
                yield return candidate;
            }
        }
    }

    private static IEnumerable<string> BuildCandidates(string directory, string name, IReadOnlyList<string> extensions)
    {
        if (Path.HasExtension(name) || extensions.Count == 0)
        {
            yield return Path.Combine(directory, name);
            yield break;
        }

        foreach (var extension in extensions)
        {
            yield return Path.Combine(directory, name + extension);
        }
    }

    private static bool LooksLikeExplicitPath(string name)
    {
        return name.Contains(Path.DirectorySeparatorChar) || name.Contains(Path.AltDirectorySeparatorChar);
    }

    private static bool IsExecutable(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var extension = Path.GetExtension(path);

            if (string.IsNullOrWhiteSpace(extension))
            {
                return false;
            }

            return GetExecutableExtensions().Contains(extension, StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            var mode = File.GetUnixFileMode(path);
            return (mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
        }
        catch
        {
            return false;
        }
    }

    private static IReadOnlyList<string> GetExecutableExtensions()
    {
        if (!OperatingSystem.IsWindows())
        {
            return [string.Empty];
        }

        var pathext = Environment.GetEnvironmentVariable("PATHEXT");

        if (string.IsNullOrWhiteSpace(pathext))
        {
            return [".exe", ".cmd", ".bat", ".com"];
        }

        return pathext
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
