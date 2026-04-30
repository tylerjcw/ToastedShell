namespace Tosh.Runtime;

public sealed record class TreeEntryInfo : IDisplayTreeNode, IShellEnumerableObject
{
    public string Name { get; init; } = string.Empty;

    public string? Type { get; init; }

    public string? FullPath { get; init; }

    public string? Mode { get; init; }

    public string? Permissions { get; init; }

    public string? User { get; init; }

    public string? Group { get; init; }

    public StorageSize? Size { get; init; }

    public long? ByteSize => Size?.Bytes;

    public DateTimeOffset? Modified { get; init; }

    public int? Inode { get; init; }

    public int? DeviceId { get; init; }

    public int? NumLinks { get; init; }

    public string? LinkTarget { get; init; }

    public int Depth { get; init; }

    public bool IsDirectory => string.Equals(Type, "dir", StringComparison.OrdinalIgnoreCase);

    public bool IsFile => string.Equals(Type, "file", StringComparison.OrdinalIgnoreCase);

    public bool IsLink => string.Equals(Type, "link", StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<TreeEntryInfo> Children { get; init; } = Array.Empty<TreeEntryInfo>();

    IEnumerable<object> IDisplayTreeNode.GetDisplayChildren() => Children;

    IEnumerable<object?> IShellEnumerableObject.EnumerateShellItems() => EnumerateDescendantsFlat();

    public IEnumerable<TreeEntryInfo> Flatten()
    {
        yield return this;

        foreach (var child in Children)
        {
            foreach (var descendant in child.Flatten())
            {
                yield return descendant;
            }
        }
    }

    public TreeEntryInfo? Find(string pattern)
    {
        if (IsGlobPattern(pattern))
        {
            return FindByGlob(pattern);
        }

        return FindByName(pattern);
    }

    public IReadOnlyList<TreeEntryInfo> FindAll(string pattern)
    {
        if (IsGlobPattern(pattern))
        {
            return FindAllByGlob(pattern);
        }

        return FindAllByName(pattern);
    }

    public TreeEntryInfo SortChildren(Comparison<TreeEntryInfo> comparison)
    {
        if (Children.Count <= 1)
        {
            return this;
        }

        var sorted = Children.ToList();
        sorted.Sort(comparison);
        return this with
        {
            Children = sorted.Select(child => child.SortChildren(comparison)).ToArray(),
        };
    }

    public override string ToString()
    {
        return IsDirectory ? $"{Name}/" : Name;
    }

    private IEnumerable<TreeEntryInfo> EnumerateDescendantsFlat()
    {
        foreach (var child in Children)
        {
            yield return child with { Children = Array.Empty<TreeEntryInfo>() };

            foreach (var descendant in child.EnumerateDescendantsFlat())
            {
                yield return descendant;
            }
        }
    }

    private TreeEntryInfo? FindByName(string name)
    {
        if (string.Equals(Name, name, StringComparison.OrdinalIgnoreCase))
        {
            return this;
        }

        foreach (var child in Children)
        {
            var found = child.FindByName(name);

            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private TreeEntryInfo? FindByGlob(string pattern)
    {
        if (GlobPatternMatcher.IsMatch(Name, pattern, ignoreCase: true))
        {
            return this;
        }

        foreach (var child in Children)
        {
            var found = child.FindByGlob(pattern);

            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private IReadOnlyList<TreeEntryInfo> FindAllByName(string name)
    {
        var results = new List<TreeEntryInfo>();
        CollectByName(name, results);
        return results;
    }

    private IReadOnlyList<TreeEntryInfo> FindAllByGlob(string pattern)
    {
        var results = new List<TreeEntryInfo>();
        CollectByGlob(pattern, results);
        return results;
    }

    private void CollectByName(string name, List<TreeEntryInfo> results)
    {
        if (string.Equals(Name, name, StringComparison.OrdinalIgnoreCase))
        {
            results.Add(this);
        }

        foreach (var child in Children)
        {
            child.CollectByName(name, results);
        }
    }

    private void CollectByGlob(string pattern, List<TreeEntryInfo> results)
    {
        if (GlobPatternMatcher.IsMatch(Name, pattern, ignoreCase: true))
        {
            results.Add(this);
        }

        foreach (var child in Children)
        {
            child.CollectByGlob(pattern, results);
        }
    }

    private static bool IsGlobPattern(string text) =>
        text.Contains('*') || text.Contains('?') || text.Contains('[');
}
