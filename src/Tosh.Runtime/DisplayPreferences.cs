namespace Tosh.Runtime;

public sealed class DisplayPreferences
{
    public DisplayPreferences()
    {
        DateTime = new TemporalDisplayPreferences(
            ScalarMode: TemporalDisplayMode.Iso,
            TableMode: TemporalDisplayMode.Relative);
        DateTimeOffset = new TemporalDisplayPreferences(
            ScalarMode: TemporalDisplayMode.Iso,
            TableMode: TemporalDisplayMode.Relative);
        DateOnly = new DateOnlyDisplayPreferences(
            ScalarMode: DateOnlyDisplayMode.Long,
            TableMode: DateOnlyDisplayMode.Iso);
        TimeOnly = new TimeOnlyDisplayPreferences(
            ScalarMode: TimeOnlyDisplayMode.TwelveHour,
            TableMode: TimeOnlyDisplayMode.TwentyFourHour);
        TimeSpan = new DurationDisplayPreferences(
            ScalarMode: DurationDisplayMode.Long,
            TableMode: DurationDisplayMode.Short);
        StorageSize = new StorageSizeDisplayPreferences();
        UnixFileMode = new UnixFileModeDisplayPreferences();
        FileAttributes = new FileAttributesDisplayPreferences();
        Profiles = new DisplayProfilePreferences();
    }

    public TemporalDisplayPreferences DateTime { get; }

    public TemporalDisplayPreferences DateTimeOffset { get; }

    public DateOnlyDisplayPreferences DateOnly { get; }

    public TimeOnlyDisplayPreferences TimeOnly { get; }

    public DurationDisplayPreferences TimeSpan { get; }

    public StorageSizeDisplayPreferences StorageSize { get; }

    public UnixFileModeDisplayPreferences UnixFileMode { get; }

    public FileAttributesDisplayPreferences FileAttributes { get; }

    public DisplayProfilePreferences Profiles { get; }

    public Func<System.DateTimeOffset> NowProvider { get; set; } = static () => System.DateTimeOffset.Now;
}

public sealed class DurationDisplayPreferences(
    DurationDisplayMode ScalarMode,
    DurationDisplayMode TableMode,
    string? ScalarFormat = null,
    string? TableFormat = null)
{
    public DurationDisplayMode ScalarMode { get; set; } = ScalarMode;

    public DurationDisplayMode TableMode { get; set; } = TableMode;

    public string? ScalarFormat { get; set; } = ScalarFormat;

    public string? TableFormat { get; set; } = TableFormat;

    public void Reset()
    {
        ScalarMode = DurationDisplayMode.Long;
        TableMode = DurationDisplayMode.Short;
        ScalarFormat = null;
        TableFormat = null;
    }
}

public sealed class DateOnlyDisplayPreferences(
    DateOnlyDisplayMode ScalarMode,
    DateOnlyDisplayMode TableMode,
    string? ScalarFormat = null,
    string? TableFormat = null)
{
    public DateOnlyDisplayMode ScalarMode { get; set; } = ScalarMode;

    public DateOnlyDisplayMode TableMode { get; set; } = TableMode;

    public string? ScalarFormat { get; set; } = ScalarFormat;

    public string? TableFormat { get; set; } = TableFormat;

    public void Reset()
    {
        ScalarMode = DateOnlyDisplayMode.Long;
        TableMode = DateOnlyDisplayMode.Iso;
        ScalarFormat = null;
        TableFormat = null;
    }
}

public sealed class TimeOnlyDisplayPreferences(
    TimeOnlyDisplayMode ScalarMode,
    TimeOnlyDisplayMode TableMode,
    string? ScalarFormat = null,
    string? TableFormat = null)
{
    public TimeOnlyDisplayMode ScalarMode { get; set; } = ScalarMode;

    public TimeOnlyDisplayMode TableMode { get; set; } = TableMode;

    public string? ScalarFormat { get; set; } = ScalarFormat;

    public string? TableFormat { get; set; } = TableFormat;

    public void Reset()
    {
        ScalarMode = TimeOnlyDisplayMode.TwelveHour;
        TableMode = TimeOnlyDisplayMode.TwentyFourHour;
        ScalarFormat = null;
        TableFormat = null;
    }
}

public sealed class DisplayProfilePreferences
{
    private readonly Dictionary<string, DisplayTypeProfilePreference> _typeProfiles = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<DisplayTypeProfilePreference> TypeProfiles => _typeProfiles.Values;

    public DisplayTypeProfilePreference GetOrCreate(string typeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(typeName);

        if (_typeProfiles.TryGetValue(typeName, out var existing))
        {
            return existing;
        }

        var created = new DisplayTypeProfilePreference(typeName);
        _typeProfiles[typeName] = created;
        return created;
    }

    public bool TryGet(string typeName, out DisplayTypeProfilePreference profile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(typeName);
        return _typeProfiles.TryGetValue(typeName, out profile!);
    }

    public bool TryResolve(Type rowType, object? sample, out DisplayTypeProfilePreference profile)
    {
        ArgumentNullException.ThrowIfNull(rowType);

        foreach (var candidate in GetCandidateNames(rowType, sample))
        {
            if (_typeProfiles.TryGetValue(candidate, out profile!))
            {
                return true;
            }
        }

        profile = null!;
        return false;
    }

    public bool Remove(string typeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(typeName);
        return _typeProfiles.Remove(typeName);
    }

    public void Reset()
    {
        _typeProfiles.Clear();
    }

    private static IEnumerable<string> GetCandidateNames(Type rowType, object? sample)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in GetNamesForObject(sample))
        {
            if (seen.Add(name))
            {
                yield return name;
            }
        }

        foreach (var name in GetNamesForType(rowType))
        {
            if (seen.Add(name))
            {
                yield return name;
            }
        }

        if (sample is not null && sample.GetType() != rowType)
        {
            foreach (var name in GetNamesForType(sample.GetType()))
            {
                if (seen.Add(name))
                {
                    yield return name;
                }
            }
        }
    }

    private static IEnumerable<string> GetNamesForObject(object? sample)
    {
        if (sample is null)
        {
            yield break;
        }

        if (sample is IShellTypedObject typed)
        {
            yield return typed.ShellTypeDescriptor.ShellTypeName;
            yield return typed.ShellTypeDescriptor.ShellFullName;
        }

        if (sample is IShellTypeDescriptor descriptor)
        {
            yield return descriptor.ShellTypeName;
            yield return descriptor.ShellFullName;
        }

        if (BuiltInShellTypes.TryDescribeRuntimeValue(sample, out var builtInDescriptor))
        {
            // Every alias, not just the current name, so a profile keyed by an
            // older spelling keeps applying — `table` for `record` after
            // TS-P3-11, and `map` for `dict`.
            foreach (var alias in BuiltInShellTypes.AliasesFor(builtInDescriptor.ShellTypeName))
            {
                yield return alias;
            }

            yield return builtInDescriptor.ShellFullName;
        }

        if (sample is IShellRecordObject shellRecord)
        {
            yield return shellRecord.ShellTypeName;
        }
    }

    private static IEnumerable<string> GetNamesForType(Type type)
    {
        if (!string.IsNullOrWhiteSpace(type.FullName))
        {
            yield return type.FullName!;
        }

        yield return type.Name;
    }
}

public sealed class DisplayTypeProfilePreference(string typeName)
{
    private readonly List<string> _tableColumns = [];

    public string TypeName { get; } = typeName;

    public IReadOnlyList<string> TableColumns => _tableColumns;

    public void SetTableColumns(IEnumerable<string> columns)
    {
        ArgumentNullException.ThrowIfNull(columns);

        _tableColumns.Clear();

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var column in columns)
        {
            if (string.IsNullOrWhiteSpace(column))
            {
                continue;
            }

            var trimmed = column.Trim();

            if (seen.Add(trimmed))
            {
                _tableColumns.Add(trimmed);
            }
        }
    }
}

public sealed class TemporalDisplayPreferences(
    TemporalDisplayMode ScalarMode,
    TemporalDisplayMode TableMode,
    string? ScalarFormat = null,
    string? TableFormat = null)
{
    public TemporalDisplayMode ScalarMode { get; set; } = ScalarMode;

    public TemporalDisplayMode TableMode { get; set; } = TableMode;

    public string? ScalarFormat { get; set; } = ScalarFormat;

    public string? TableFormat { get; set; } = TableFormat;

    public void Reset()
    {
        ScalarMode = TemporalDisplayMode.Iso;
        TableMode = TemporalDisplayMode.Relative;
        ScalarFormat = null;
        TableFormat = null;
    }
}

public enum TemporalDisplayMode
{
    Iso,
    Local,
    Relative,
    Unix,
    Custom,
}

public enum DateOnlyDisplayMode
{
    Iso,
    Long,
    Relative,
    Custom,
}

public enum TimeOnlyDisplayMode
{
    TwentyFourHour,
    TwelveHour,
    Custom,
}

public enum DurationDisplayMode
{
    Raw,
    Short,
    Long,
    TotalSeconds,
    Custom,
}

public sealed class StorageSizeDisplayPreferences
{
    public StorageSizeDisplayMode Mode { get; set; } = StorageSizeDisplayMode.Human;

    public void Reset()
    {
        Mode = StorageSizeDisplayMode.Human;
    }
}

public enum StorageSizeDisplayMode
{
    Human,
    Bytes,
}

public sealed class UnixFileModeDisplayPreferences
{
    public UnixFileModeDisplayMode Mode { get; set; } = UnixFileModeDisplayMode.Symbolic;

    public void Reset()
    {
        Mode = UnixFileModeDisplayMode.Symbolic;
    }
}

public enum UnixFileModeDisplayMode
{
    Symbolic,
    Octal,
    Both,
}

public sealed class FileAttributesDisplayPreferences
{
    public FileAttributesDisplayMode Mode { get; set; } = FileAttributesDisplayMode.Names;

    public void Reset()
    {
        Mode = FileAttributesDisplayMode.Names;
    }
}

public enum FileAttributesDisplayMode
{
    Names,
    Hex,
    Both,
}
