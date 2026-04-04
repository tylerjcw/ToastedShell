namespace Tosh.Core;

/// <summary>
/// Describes a single file-system entry that was deleted.
/// FullName is available but hidden from default display.
/// For directories, Children forms the tree hierarchy.
/// </summary>
public sealed class RemovedEntry : IShellRecordObject, IDisplayTreeNode
{
    public RemovedEntry(string name, string fullName, bool isDirectory, StorageSize size)
    {
        Name = name;
        FullName = fullName;
        IsDirectory = isDirectory;
        Size = size;
    }

    public string Name { get; }
    public string FullName { get; }
    public bool IsDirectory { get; }
    public StorageSize Size { get; }
    public IReadOnlyList<RemovedEntry> Children { get; init; } = [];

    public string ShellTypeName => "RemovedEntry";

    IEnumerable<object> IDisplayTreeNode.GetDisplayChildren() => Children;

    public bool TryGetMember(string name, out object? value, bool includeHidden = false)
    {
        value = name.ToLowerInvariant() switch
        {
            "name" => Name,
            "fullname" => FullName,
            "isdirectory" => IsDirectory,
            "size" => Size,
            "children" => Children,
            _ => null,
        };

        return value is not null;
    }

    public bool TrySetMember(string name, object? value) => false;

    public IReadOnlyList<KeyValuePair<string, object?>> GetMembers(bool includeHidden = false)
    {
        var members = new List<KeyValuePair<string, object?>>
        {
            new("Name", Name),
            new("IsDirectory", IsDirectory),
            new("Size", Size),
        };

        if (includeHidden)
        {
            members.Add(new("FullName", FullName));
        }

        return members;
    }

    public override string ToString() => IsDirectory ? $"{Name}/" : Name;
}

/// <summary>
/// Result returned by the rm command for each top-level path it processes.
/// When a directory is deleted recursively, <see cref="Children"/> contains
/// every descendant that was removed. Children is hidden from display when empty.
/// </summary>
public sealed class RemovalResult : IShellRecordObject
{
    public RemovalResult(string fullName, bool isDirectory, StorageSize size, IReadOnlyList<RemovedEntry> children)
    {
        FullName = fullName;
        Name = System.IO.Path.GetFileName(fullName);
        IsDirectory = isDirectory;
        Size = size;
        Children = children;
    }

    public string Name { get; }
    public string FullName { get; }
    public bool IsDirectory { get; }
    public StorageSize Size { get; }
    public IReadOnlyList<RemovedEntry> Children { get; }

    public string ShellTypeName => "RemovalResult";

    public bool TryGetMember(string name, out object? value, bool includeHidden = false)
    {
        value = name.ToLowerInvariant() switch
        {
            "name" => Name,
            "fullname" => FullName,
            "isdirectory" => IsDirectory,
            "size" => Size,
            "children" => Children,
            _ => null,
        };

        return value is not null || name.Equals("Children", StringComparison.OrdinalIgnoreCase);
    }

    public bool TrySetMember(string name, object? value) => false;

    public IReadOnlyList<KeyValuePair<string, object?>> GetMembers(bool includeHidden = false)
    {
        var members = new List<KeyValuePair<string, object?>>
        {
            new("Name", Name),
            new("FullName", FullName),
            new("IsDirectory", IsDirectory),
            new("Size", Size),
        };

        if (Children.Count > 0)
        {
            members.Add(new("Children", Children));
        }

        return members;
    }

    public override string ToString() => $"RemovalResult {{ Name = {Name} }}";
}
