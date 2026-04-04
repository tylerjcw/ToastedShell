namespace Tosh.Core;

public sealed record FileSystemEntry : IDisplayTreeNode
{
    private readonly UnixFileSystemMetadata? _unixMetadata;

    public FileSystemEntry(
        FileSystemInfo entry,
        bool preferLongDisplay = false,
        bool classifyNames = false,
        bool includeInodeInShortDisplay = false,
        FileSystemEntryTimeField listingTimeField = FileSystemEntryTimeField.Modified)
        : this(entry, preferLongDisplay, classifyNames, includeInodeInShortDisplay, listingTimeField, UnixFileSystemMetadata.TryRead(entry))
    {
    }

    private FileSystemEntry(
        FileSystemInfo entry,
        bool preferLongDisplay,
        bool classifyNames,
        bool includeInodeInShortDisplay,
        FileSystemEntryTimeField listingTimeField,
        UnixFileSystemMetadata? unixMetadata)
    {
        Entry = entry ?? throw new ArgumentNullException(nameof(entry));
        PreferLongDisplay = preferLongDisplay;
        ClassifyNames = classifyNames;
        IncludeInodeInShortDisplay = includeInodeInShortDisplay;
        ListingTimeField = listingTimeField;
        _unixMetadata = unixMetadata;
    }

    public FileSystemInfo Entry { get; }

    public bool PreferLongDisplay { get; }

    public bool ClassifyNames { get; }

    public bool IncludeInodeInShortDisplay { get; }

    public FileSystemEntryTimeField ListingTimeField { get; }

    public IReadOnlyList<FileSystemEntry> Children { get; init; } = Array.Empty<FileSystemEntry>();

    IEnumerable<object> IDisplayTreeNode.GetDisplayChildren() => Children;

    public string Name => Entry.Name;

    public string FullName => Entry.FullName;

    public bool Exists => Entry.Exists;

    public bool IsDirectory => Entry is DirectoryInfo;

    public bool IsFile => Entry is FileInfo;

    public bool IsSymbolicLink => Target is not null;

    public FileSystemEntryType Kind => IsDirectory ? FileSystemEntryType.Dir : FileSystemEntryType.File;

    public FileSystemEntryType Type => Kind;

    public string Extension => Entry.Extension;

    public bool IsHidden => IsHiddenEntry(Entry);

    public bool IsReadOnly => Entry.Attributes.HasFlag(FileAttributes.ReadOnly);

    public bool Readonly => IsReadOnly;

    public StorageSize? Length => Entry is FileInfo file ? StorageSize.FromBytes(file.Length) : null;

    public StorageSize? Size => Length;

    public long? ByteLength => Entry is FileInfo file ? file.Length : null;

    public long? Bytes => ByteLength;

    public DateTime CreationTime => Entry.CreationTime;

    public DateTimeOffset Created => ToInstant(CreationTime);

    public DateTime LastAccessTime => Entry.LastAccessTime;

    public DateTimeOffset Accessed => ToInstant(LastAccessTime);

    public DateTime LastWriteTime => Entry.LastWriteTime;

    public DateTimeOffset Modified => ToInstant(LastWriteTime);

    public DateTime LastWriteTimeUtc => Entry.LastWriteTimeUtc;

    public FileAttributes Attributes => Entry.Attributes;

    public UnixFileMode? Permissions => TryGetUnixFileMode(Entry);

    public UnixFileMode? Mode => Permissions;

    public FileSystemPrincipalInfo? Owner => _unixMetadata?.Owner;

    public FileSystemPrincipalInfo? User => Owner;

    public string? OwnerName => Owner?.Name;

    public string? UserName => User?.Name;

    public long? OwnerId => Owner?.Id;

    public long? UserId => User?.Id;

    public FileSystemPrincipalInfo? Group => _unixMetadata?.Group;

    public string? GroupName => Group?.Name;

    public long? GroupId => Group?.Id;

    public long? LinkCount => _unixMetadata?.LinkCount;

    public long? NumLinks => LinkCount;

    public long? Inode => _unixMetadata?.Inode;

    public string? LinkTarget => TryGetLinkTarget(Entry);

    public string? Target => LinkTarget;

    public DateTimeOffset DisplayTime => ListingTimeField switch
    {
        FileSystemEntryTimeField.Accessed => Accessed,
        FileSystemEntryTimeField.Created => Created,
        _ => Modified,
    };

    public string DisplayTimeColumnName => ListingTimeField switch
    {
        FileSystemEntryTimeField.Accessed => "Accessed",
        FileSystemEntryTimeField.Created => "Created",
        _ => "Modified",
    };

    public string DisplayName => GetDisplayName();

    public string ReadAllText()
    {
        EnsureReadableFile("ReadAllText");
        return File.ReadAllText(Entry.FullName);
    }

    public string[] ReadAllLines()
    {
        EnsureReadableFile("ReadAllLines");
        return File.ReadAllLines(Entry.FullName);
    }

    public byte[] ReadAllBytes()
    {
        EnsureReadableFile("ReadAllBytes");
        return File.ReadAllBytes(Entry.FullName);
    }

    public ManagedFileHandle OpenText()
    {
        EnsureReadableFile("OpenText");
        return ManagedFileHandle.OpenTextRead(Entry.FullName);
    }

    public ManagedFileHandle OpenRead()
    {
        EnsureReadableFile("OpenRead");
        return ManagedFileHandle.OpenBinaryRead(Entry.FullName);
    }

    public ManagedFileHandle OpenWrite()
    {
        EnsureFilePath("OpenWrite");
        return ManagedFileHandle.OpenBinaryWrite(Entry.FullName, append: false);
    }

    public ManagedFileHandle OpenAppend()
    {
        EnsureFilePath("OpenAppend");
        return ManagedFileHandle.OpenBinaryWrite(Entry.FullName, append: true);
    }

    public static FileSystemEntry From(
        FileSystemInfo entry,
        bool preferLongDisplay = false,
        bool classifyNames = false,
        bool includeInodeInShortDisplay = false,
        FileSystemEntryTimeField listingTimeField = FileSystemEntryTimeField.Modified)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return new FileSystemEntry(entry, preferLongDisplay, classifyNames, includeInodeInShortDisplay, listingTimeField);
    }

    internal string GetModeDisplay(bool includeTypeIndicator)
    {
        if (Permissions is UnixFileMode permissions)
        {
            return FormatModeString(GetTypeIndicator(), permissions, includeTypeIndicator);
        }

        return FormatFallbackModeString(this, includeTypeIndicator);
    }

    private static bool IsHiddenEntry(FileSystemInfo entry)
    {
        return entry.Name.StartsWith(".", StringComparison.Ordinal) || entry.Attributes.HasFlag(FileAttributes.Hidden);
    }

    private string GetDisplayName()
    {
        if (ClassifyNames && IsSymbolicLink)
        {
            return $"{Name}@";
        }

        if (IsDirectory)
        {
            return Name.EndsWith(Path.DirectorySeparatorChar) ? Name : $"{Name}/";
        }

        if (ClassifyNames && IsExecutableEntry())
        {
            return $"{Name}*";
        }

        return Name;
    }

    private void EnsureReadableFile(string operation)
    {
        if (Entry is not FileInfo)
        {
            throw new InvalidOperationException($"{operation} requires a file entry, but '{FullName}' is not a file.");
        }

        if (!Entry.Exists)
        {
            throw new InvalidOperationException($"File '{FullName}' does not exist.");
        }
    }

    private void EnsureFilePath(string operation)
    {
        if (Entry is not FileInfo)
        {
            throw new InvalidOperationException($"{operation} requires a file entry, but '{FullName}' is not a file.");
        }
    }

    private static string? TryGetLinkTarget(FileSystemInfo entry)
    {
        try
        {
            return entry.LinkTarget;
        }
        catch
        {
            return null;
        }
    }

    private static UnixFileMode? TryGetUnixFileMode(FileSystemInfo entry)
    {
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                return File.GetUnixFileMode(entry.FullName);
            }
            catch
            {
            }
        }

        return null;
    }

    private char GetTypeIndicator()
    {
        if (IsSymbolicLink)
        {
            return 'l';
        }

        return Kind == FileSystemEntryType.Dir ? 'd' : '-';
    }

    private static DateTimeOffset ToInstant(DateTime value)
    {
        return value.Kind switch
        {
            DateTimeKind.Utc => new DateTimeOffset(value, TimeSpan.Zero),
            DateTimeKind.Local => new DateTimeOffset(value),
            _ => new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Local)),
        };
    }

    private static string FormatModeString(char typeIndicator, UnixFileMode mode, bool includeTypeIndicator)
    {
        Span<char> characters = stackalloc char[includeTypeIndicator ? 10 : 9];
        var offset = 0;

        if (includeTypeIndicator)
        {
            characters[offset++] = typeIndicator;
        }

        characters[offset++] = HasMode(mode, UnixFileMode.UserRead) ? 'r' : '-';
        characters[offset++] = HasMode(mode, UnixFileMode.UserWrite) ? 'w' : '-';
        characters[offset++] = GetExecuteCharacter(mode, UnixFileMode.UserExecute, UnixFileMode.SetUser, 's', 'S');
        characters[offset++] = HasMode(mode, UnixFileMode.GroupRead) ? 'r' : '-';
        characters[offset++] = HasMode(mode, UnixFileMode.GroupWrite) ? 'w' : '-';
        characters[offset++] = GetExecuteCharacter(mode, UnixFileMode.GroupExecute, UnixFileMode.SetGroup, 's', 'S');
        characters[offset++] = HasMode(mode, UnixFileMode.OtherRead) ? 'r' : '-';
        characters[offset++] = HasMode(mode, UnixFileMode.OtherWrite) ? 'w' : '-';
        characters[offset] = GetExecuteCharacter(mode, UnixFileMode.OtherExecute, UnixFileMode.StickyBit, 't', 'T');

        return new string(characters);
    }

    private static string FormatFallbackModeString(FileSystemEntry entry, bool includeTypeIndicator)
    {
        var prefix = includeTypeIndicator ? entry.GetTypeIndicator().ToString() : string.Empty;
        var writable = !entry.Attributes.HasFlag(FileAttributes.ReadOnly);
        var executable = entry.IsDirectory || IsExecutableName(entry.Name);
        var triplet = $"r{(writable ? 'w' : '-')}{(executable ? 'x' : '-')}";
        return $"{prefix}{triplet}{triplet}{triplet}";
    }

    private static bool HasMode(UnixFileMode value, UnixFileMode flag) => (value & flag) == flag;

    private static char GetExecuteCharacter(
        UnixFileMode mode,
        UnixFileMode executeFlag,
        UnixFileMode specialFlag,
        char specialWhenExecute,
        char specialWhenNotExecute)
    {
        var hasExecute = HasMode(mode, executeFlag);
        var hasSpecial = HasMode(mode, specialFlag);

        if (hasSpecial)
        {
            return hasExecute ? specialWhenExecute : specialWhenNotExecute;
        }

        return hasExecute ? 'x' : '-';
    }

    private static bool IsExecutableName(string name)
    {
        var extension = Path.GetExtension(name);

        return extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".bat", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".sh", StringComparison.OrdinalIgnoreCase);
    }

    private bool IsExecutableEntry()
    {
        if (Permissions is UnixFileMode mode)
        {
            return HasMode(mode, UnixFileMode.UserExecute) ||
                   HasMode(mode, UnixFileMode.GroupExecute) ||
                   HasMode(mode, UnixFileMode.OtherExecute);
        }

        return IsExecutableName(Name);
    }
}

public enum FileSystemEntryTimeField
{
    Modified,
    Accessed,
    Created,
}
