namespace Tosh.Runtime;

public static class PathUtilities
{
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static string UserHomeDirectory =>
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static readonly AsyncLocal<ToshDirectoryAliasConfig?> _directoryAliases = new();

    public static ToshDirectoryAliasConfig? DirectoryAliases
    {
        get => _directoryAliases.Value;
        set => _directoryAliases.Value = value;
    }

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

    public static bool ContainsGlobPattern(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        for (var index = 0; index < path.Length; index++)
        {
            if (path[index] is '*' or '?' or '[')
            {
                return true;
            }

            if (path[index] == '@' && index + 1 < path.Length && path[index + 1] == '(')
            {
                return true;
            }
        }

        return false;
    }

    public static IReadOnlyList<GlobPathMatch> ExpandGlob(
        string currentDirectory,
        string pattern,
        bool includeHidden = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currentDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);

        var expandedPattern = ExpandHomeDirectory(pattern);
        var expandedPatterns = ExpandAlternationPatterns(expandedPattern);
        var results = new List<GlobPathMatch>();
        var seen = new HashSet<string>(PathComparer);

        foreach (var alternative in expandedPatterns)
        {
            foreach (var match in ExpandSinglePattern(currentDirectory, alternative, includeHidden))
            {
                if (seen.Add(match.FullPath))
                {
                    results.Add(match);
                }
            }
        }

        results.Sort(static (left, right) =>
            StringComparerFromPlatform().Compare(left.ArgumentText, right.ArgumentText));
        return results;
    }

    /// <summary>What <see cref="ExpandTilde"/> made of a word.</summary>
    public enum TildeExpansionKind
    {
        /// <summary>No leading <c>~</c>, so the word is unchanged.</summary>
        NotATilde,

        /// <summary>Expanded to a directory.</summary>
        Expanded,

        /// <summary>A <c>~name</c> naming neither a directory alias nor a user.</summary>
        UnknownName,
    }

    /// <summary>The one implementation of the tilde rule.</summary>
    /// <param name="Kind">What happened.</param>
    /// <param name="Path">The expanded path, or the original word when nothing was expanded.</param>
    /// <param name="Name">The unresolved name, when <paramref name="Kind"/> is
    /// <see cref="TildeExpansionKind.UnknownName"/>.</param>
    public readonly record struct TildeExpansion(TildeExpansionKind Kind, string Path, string Name);

    /// <summary>
    /// Expands a leading <c>~</c>: alone, before a separator, or naming a directory alias or a
    /// user.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The tilde is only recognised at the start of the word, so a backup file called
    /// <c>notes.txt~</c> and a <c>--flag=~/x</c> are both left alone.
    /// </para>
    /// <para>
    /// A configured directory alias beats a user of the same name: the alias was written down on
    /// purpose by whoever runs the shell, and the accounts on the machine were not.
    /// </para>
    /// <para>
    /// This is the only place the rule is written. Both callers — path resolution inside a
    /// command, and the shell's own argument expansion — read the same answer and differ only in
    /// what they do about <see cref="TildeExpansionKind.UnknownName"/>, which is a policy rather
    /// than a rule (<c>TS-P1-24</c>).
    /// </para>
    /// </remarks>
    public static TildeExpansion ExpandTilde(string path)
    {
        if (string.IsNullOrEmpty(path) || path[0] != '~')
        {
            return new TildeExpansion(TildeExpansionKind.NotATilde, path, string.Empty);
        }

        if (path.Length == 1)
        {
            return new TildeExpansion(TildeExpansionKind.Expanded, UserHomeDirectory, string.Empty);
        }

        if (path[1] is '/' or '\\')
        {
            return new TildeExpansion(
                TildeExpansionKind.Expanded,
                Path.Combine(UserHomeDirectory, path[2..]),
                string.Empty);
        }

        var separatorIndex = path.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], 1);
        var name = separatorIndex < 0 ? path[1..] : path[1..separatorIndex];
        var remainder = separatorIndex < 0 ? string.Empty : path[(separatorIndex + 1)..];

        if (DirectoryAliases is not null && DirectoryAliases.TryResolve(name, out var alias))
        {
            return new TildeExpansion(TildeExpansionKind.Expanded, Combine(alias, remainder), name);
        }

        if (TryResolveUserHome(name, out var userHome))
        {
            return new TildeExpansion(TildeExpansionKind.Expanded, Combine(userHome, remainder), name);
        }

        return new TildeExpansion(TildeExpansionKind.UnknownName, path, name);

        static string Combine(string root, string remainder) =>
            remainder.Length == 0 ? root : Path.Combine(root, remainder);
    }

    /// <summary>Finds a user's home directory by name.</summary>
    /// <remarks>
    /// The current user is answered from the environment, which is both the common case and the
    /// one that has to work when accounts live somewhere other than <c>/etc/passwd</c>. Everyone
    /// else is looked up in the passwd file, parsed rather than shelled out to — a shell that
    /// spawns a process to expand an argument would pay for it on every word.
    /// </remarks>
    private static bool TryResolveUserHome(string name, out string home)
    {
        if (string.Equals(name, Environment.UserName, PathComparison))
        {
            home = UserHomeDirectory;
            return true;
        }

        return TryResolveUserHomeFromPasswd(name, out home);
    }

    private static readonly Lock PasswdLock = new();
    private static Dictionary<string, string>? _passwdHomes;
    private static DateTime _passwdReadAt;

    private static bool TryResolveUserHomeFromPasswd(string name, out string home)
    {
        home = string.Empty;

        if (OperatingSystem.IsWindows())
        {
            return false;
        }

        const string PasswdPath = "/etc/passwd";

        try
        {
            var writtenAt = File.GetLastWriteTimeUtc(PasswdPath);

            lock (PasswdLock)
            {
                if (_passwdHomes is null || writtenAt != _passwdReadAt)
                {
                    _passwdHomes = ReadPasswdHomes(PasswdPath);
                    _passwdReadAt = writtenAt;
                }

                return _passwdHomes.TryGetValue(name, out home!) && !string.IsNullOrEmpty(home);
            }
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static Dictionary<string, string> ReadPasswdHomes(string passwdPath)
    {
        var homes = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var line in File.ReadLines(passwdPath))
        {
            // name:password:uid:gid:gecos:home:shell — the gecos field may contain anything,
            // so the fields are counted rather than searched.
            var fields = line.Split(':');

            if (fields.Length >= 6 && fields[0].Length > 0 && fields[5].Length > 0)
            {
                homes[fields[0]] = fields[5];
            }
        }

        return homes;
    }

    private static string ExpandHomeDirectory(string path)
    {
        // An unresolved `~name` stays literal here. The shell's argument expansion refuses it
        // with a diagnostic before a command ever sees one, so this is the inner path taken by
        // text that never passed through argument expansion at all.
        var expansion = ExpandTilde(path);
        return expansion.Kind == TildeExpansionKind.Expanded ? expansion.Path : path;
    }

    private static IEnumerable<GlobPathMatch> ExpandSinglePattern(
        string currentDirectory,
        string pattern,
        bool includeHidden)
    {
        var pathRoot = Path.GetPathRoot(pattern) ?? string.Empty;
        var isAbsolute = Path.IsPathRooted(pattern);
        var remainder = pattern[pathRoot.Length..];
        var segments = SplitPathSegments(remainder);

        var firstGlobIndex = -1;

        for (var index = 0; index < segments.Length; index++)
        {
            if (IsGlobSegment(segments[index]))
            {
                firstGlobIndex = index;
                break;
            }
        }

        if (firstGlobIndex < 0)
        {
            var absolutePath = ResolvePath(currentDirectory, pattern);

            if (File.Exists(absolutePath) || Directory.Exists(absolutePath))
            {
                yield return new GlobPathMatch(
                    ArgumentText: isAbsolute ? absolutePath : pattern,
                    FullPath: absolutePath);
            }

            yield break;
        }

        var literalPrefixSegments = segments[..firstGlobIndex];
        var patternSegments = segments[firstGlobIndex..];
        var searchRoot = ResolveGlobSearchRoot(currentDirectory, pathRoot, literalPrefixSegments, isAbsolute);
        var relativePrefixSegments = isAbsolute ? Array.Empty<string>() : literalPrefixSegments;

        if (!Directory.Exists(searchRoot))
        {
            yield break;
        }

        foreach (var match in EnumerateGlobMatches(
                     searchRoot,
                     relativePrefixSegments,
                     patternSegments,
                     isAbsolute,
                     includeHidden))
        {
            yield return match;
        }
    }

    private static IEnumerable<GlobPathMatch> EnumerateGlobMatches(
        string currentSearchDirectory,
        IReadOnlyList<string> literalPrefixSegments,
        IReadOnlyList<string> patternSegments,
        bool isAbsolute,
        bool includeHidden)
    {
        foreach (var match in EnumerateGlobMatchesCore(
                     currentSearchDirectory,
                     literalPrefixSegments.ToList(),
                     patternSegments,
                     0,
                     isAbsolute,
                     includeHidden))
        {
            yield return match;
        }
    }

    private static IEnumerable<GlobPathMatch> EnumerateGlobMatchesCore(
        string currentSearchDirectory,
        List<string> relativeSegments,
        IReadOnlyList<string> patternSegments,
        int patternIndex,
        bool isAbsolute,
        bool includeHidden)
    {
        if (patternIndex >= patternSegments.Count)
        {
            if (relativeSegments.Count == 0)
            {
                yield break;
            }

            var pathText = BuildRelativePath(relativeSegments);
            var fullPath = currentSearchDirectory;

            yield return new GlobPathMatch(
                ArgumentText: isAbsolute ? fullPath : pathText,
                FullPath: fullPath);
            yield break;
        }

        var segmentPattern = patternSegments[patternIndex];

        if (segmentPattern == "**")
        {
            foreach (var match in EnumerateGlobMatchesCore(
                         currentSearchDirectory,
                         relativeSegments,
                         patternSegments,
                         patternIndex + 1,
                         isAbsolute,
                         includeHidden))
            {
                yield return match;
            }

            var children = EnumerateFileSystemInfosSafe(currentSearchDirectory);
            var isFinalPattern = patternIndex == patternSegments.Count - 1;

            foreach (var child in children)
            {
                if (!includeHidden && child.Name.StartsWith(".", StringComparison.Ordinal))
                {
                    continue;
                }

                if (child is DirectoryInfo directory)
                {
                    relativeSegments.Add(directory.Name);

                    if (isFinalPattern)
                    {
                        yield return new GlobPathMatch(
                            ArgumentText: isAbsolute ? directory.FullName : BuildRelativePath(relativeSegments),
                            FullPath: directory.FullName);
                    }

                    foreach (var match in EnumerateGlobMatchesCore(
                                 directory.FullName,
                                 relativeSegments,
                                 patternSegments,
                                 patternIndex,
                                 isAbsolute,
                                 includeHidden))
                    {
                        yield return match;
                    }

                    relativeSegments.RemoveAt(relativeSegments.Count - 1);
                    continue;
                }

                if (isFinalPattern)
                {
                    relativeSegments.Add(child.Name);
                    yield return new GlobPathMatch(
                        ArgumentText: isAbsolute ? child.FullName : BuildRelativePath(relativeSegments),
                        FullPath: child.FullName);
                    relativeSegments.RemoveAt(relativeSegments.Count - 1);
                }
            }

            yield break;
        }

        foreach (var child in EnumerateFileSystemInfosSafe(currentSearchDirectory))
        {
            if (!includeHidden && child.Name.StartsWith(".", StringComparison.Ordinal) && !segmentPattern.StartsWith(".", StringComparison.Ordinal))
            {
                continue;
            }

            if (!GlobPatternMatcher.IsMatch(child.Name, segmentPattern, OperatingSystem.IsWindows()))
            {
                continue;
            }

            relativeSegments.Add(child.Name);

            if (patternIndex == patternSegments.Count - 1)
            {
                yield return new GlobPathMatch(
                    ArgumentText: isAbsolute ? child.FullName : BuildRelativePath(relativeSegments),
                    FullPath: child.FullName);
            }
            else if (child is DirectoryInfo directory && directory.LinkTarget is null)
            {
                foreach (var match in EnumerateGlobMatchesCore(
                             directory.FullName,
                             relativeSegments,
                             patternSegments,
                             patternIndex + 1,
                             isAbsolute,
                             includeHidden))
                {
                    yield return match;
                }
            }

            relativeSegments.RemoveAt(relativeSegments.Count - 1);
        }
    }

    private static IEnumerable<FileSystemInfo> EnumerateFileSystemInfosSafe(string path)
    {
        if (!Directory.Exists(path))
        {
            return Array.Empty<FileSystemInfo>();
        }

        try
        {
            return new DirectoryInfo(path).EnumerateFileSystemInfos();
        }
        catch (UnauthorizedAccessException)
        {
            return Array.Empty<FileSystemInfo>();
        }
        catch (IOException)
        {
            return Array.Empty<FileSystemInfo>();
        }
    }

    private static string ResolveGlobSearchRoot(
        string currentDirectory,
        string pathRoot,
        IReadOnlyList<string> literalPrefixSegments,
        bool isAbsolute)
    {
        var literalPrefix = literalPrefixSegments.Count == 0
            ? string.Empty
            : Path.Combine(literalPrefixSegments.ToArray());

        if (isAbsolute)
        {
            if (string.IsNullOrEmpty(literalPrefix))
            {
                return Path.GetFullPath(string.IsNullOrEmpty(pathRoot) ? Path.DirectorySeparatorChar.ToString() : pathRoot);
            }

            return Path.GetFullPath(Path.Combine(pathRoot, literalPrefix));
        }

        return string.IsNullOrEmpty(literalPrefix)
            ? currentDirectory
            : ResolvePath(currentDirectory, literalPrefix);
    }

    private static string[] SplitPathSegments(string path)
    {
        return path
            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
    }

    private static bool IsGlobSegment(string segment)
    {
        return segment == "**" || ContainsGlobPattern(segment);
    }

    private static string BuildRelativePath(IReadOnlyList<string> segments)
    {
        return segments.Count switch
        {
            0 => ".",
            1 => segments[0],
            _ => Path.Combine(segments.ToArray()),
        };
    }

    private static IReadOnlyList<string> ExpandAlternationPatterns(string pattern)
    {
        var alternationStart = FindAlternationStart(pattern);

        if (alternationStart < 0)
        {
            return [pattern];
        }

        var alternationEnd = FindAlternationEnd(pattern, alternationStart + 2);

        if (alternationEnd < 0)
        {
            return [pattern];
        }

        var prefix = pattern[..alternationStart];
        var suffix = pattern[(alternationEnd + 1)..];
        var options = SplitAlternationOptions(pattern[(alternationStart + 2)..alternationEnd]);
        var results = new List<string>();

        foreach (var option in options)
        {
            foreach (var expanded in ExpandAlternationPatterns(prefix + option + suffix))
            {
                results.Add(expanded);
            }
        }

        return results;
    }

    private static int FindAlternationStart(string pattern)
    {
        for (var index = 0; index < pattern.Length - 1; index++)
        {
            if (pattern[index] == '@' && pattern[index + 1] == '(')
            {
                return index;
            }
        }

        return -1;
    }

    private static int FindAlternationEnd(string pattern, int startIndex)
    {
        var depth = 1;

        for (var index = startIndex; index < pattern.Length; index++)
        {
            if (pattern[index] == '@' && index + 1 < pattern.Length && pattern[index + 1] == '(')
            {
                depth++;
                index++;
                continue;
            }

            if (pattern[index] == ')')
            {
                depth--;

                if (depth == 0)
                {
                    return index;
                }
            }
        }

        return -1;
    }

    private static IReadOnlyList<string> SplitAlternationOptions(string optionsText)
    {
        var options = new List<string>();
        var builder = new System.Text.StringBuilder();
        var depth = 0;

        for (var index = 0; index < optionsText.Length; index++)
        {
            var character = optionsText[index];

            if (character == '@' && index + 1 < optionsText.Length && optionsText[index + 1] == '(')
            {
                depth++;
                builder.Append("@(");
                index++;
                continue;
            }

            if (character == ')' && depth > 0)
            {
                depth--;
                builder.Append(character);
                continue;
            }

            if (character == ',' && depth == 0)
            {
                options.Add(builder.ToString());
                builder.Clear();
                continue;
            }

            builder.Append(character);
        }

        options.Add(builder.ToString());
        return options;
    }

    private static StringComparer StringComparerFromPlatform() =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}

public sealed record GlobPathMatch(string ArgumentText, string FullPath);
