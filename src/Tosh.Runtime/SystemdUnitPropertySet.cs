namespace Tosh.Runtime;

public sealed class SystemdUnitPropertySet : IShellRecordObject
{
    private readonly Dictionary<string, object?> _properties;

    public SystemdUnitPropertySet(IEnumerable<KeyValuePair<string, object?>> properties)
    {
        ArgumentNullException.ThrowIfNull(properties);

        _properties = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, value) in properties)
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                _properties[name] = value;
            }
        }
    }

    public string ShellTypeName => "SystemdUnitPropertySet";

    public IReadOnlyDictionary<string, object?> Properties => _properties;

    public string? Id => GetString("Id");

    public IReadOnlyList<string> Names => GetStringList("Names");

    public string? Description => GetString("Description");

    public string? LoadState => GetString("LoadState");

    public string? ActiveState => GetString("ActiveState");

    public string? SubState => GetString("SubState");

    public string DisplayState =>
        string.Join(
            "/",
            new[] { LoadState, ActiveState, SubState }.Where(value => !string.IsNullOrWhiteSpace(value)));

    public string? UnitFileState => GetString("UnitFileState");

    public string? UnitFilePreset => GetString("UnitFilePreset");

    public string? Type => GetString("Type");

    public string? FragmentPath => GetString("FragmentPath");

    public string? ControlGroup => GetString("ControlGroup");

    public string? Slice => GetString("Slice");

    public string? Following => GetString("Following");

    public string? Result => GetString("Result");

    public IReadOnlyList<string> Documentation => GetStringList("Documentation");

    public IReadOnlyList<string> Requires => GetStringList("Requires");

    public IReadOnlyList<string> Wants => GetStringList("Wants");

    public IReadOnlyList<string> WantedBy => GetStringList("WantedBy");

    public IReadOnlyList<string> Conflicts => GetStringList("Conflicts");

    public IReadOnlyList<string> Before => GetStringList("Before");

    public IReadOnlyList<string> After => GetStringList("After");

    public IReadOnlyList<string> TriggeredBy => GetStringList("TriggeredBy");

    public IReadOnlyList<string> Triggers => GetStringList("Triggers");

    public Guid? InvocationId => GetGuid("InvocationID");

    public int? MainPid => GetInt32("MainPID");

    public int? ExecMainPid => GetInt32("ExecMainPID");

    public int? NRestarts => GetInt32("NRestarts");

    public int? ExecMainStatus => GetInt32("ExecMainStatus");

    public int? TasksCurrent => GetInt32("TasksCurrent");

    public StorageSize? MemoryCurrent => GetStorageSize("MemoryCurrent");

    public StorageSize? MemoryPeak => GetStorageSize("MemoryPeak");

    public DateTimeOffset? ActiveEnterTimestamp => GetDateTimeOffset("ActiveEnterTimestamp");

    public DateTimeOffset? InactiveExitTimestamp => GetDateTimeOffset("InactiveExitTimestamp");

    public DateTimeOffset? StateChangeTimestamp => GetDateTimeOffset("StateChangeTimestamp");

    public object? RestartInterval => GetValue("RestartUSec");

    public object? TimeoutStart => GetValue("TimeoutStartUSec");

    public object? TimeoutStop => GetValue("TimeoutStopUSec");

    public bool? CanStart => GetBoolean("CanStart");

    public bool? CanStop => GetBoolean("CanStop");

    public bool? CanReload => GetBoolean("CanReload");

    public bool? NeedDaemonReload => GetBoolean("NeedDaemonReload");

    public bool? ConditionResult => GetBoolean("ConditionResult");

    public bool? AssertResult => GetBoolean("AssertResult");

    public bool? Transient => GetBoolean("Transient");

    public IReadOnlyList<SystemdJournalEntry> RecentLog => GetJournalEntryList("RecentLog");

    public string UnitType => SystemdParsingUtilities.GetUnitType(Id);

    public bool IsActive => string.Equals(ActiveState, "active", StringComparison.OrdinalIgnoreCase);

    public bool IsFailed =>
        string.Equals(ActiveState, "failed", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(SubState, "failed", StringComparison.OrdinalIgnoreCase);

    public int PropertyCount => _properties.Count;

    public int RecentLogCount => RecentLog.Count;

    public bool TryGetMember(string name, out object? value, bool includeHidden = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        value = name switch
        {
            nameof(DisplayState) => DisplayState,
            nameof(UnitType) => UnitType,
            nameof(IsActive) => IsActive,
            nameof(IsFailed) => IsFailed,
            nameof(PropertyCount) => PropertyCount,
            nameof(RecentLogCount) => RecentLogCount,
            _ => null,
        };

        if (value is not null)
        {
            return true;
        }

        return _properties.TryGetValue(name, out value);
    }

    public bool TrySetMember(string name, object? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _properties[name] = value;
        return true;
    }

    public IReadOnlyList<KeyValuePair<string, object?>> GetMembers(bool includeHidden = false)
    {
        var members = new List<KeyValuePair<string, object?>>
        {
            new(nameof(DisplayState), DisplayState),
            new(nameof(UnitType), UnitType),
            new(nameof(IsActive), IsActive),
            new(nameof(IsFailed), IsFailed),
            new(nameof(PropertyCount), PropertyCount),
            new(nameof(RecentLogCount), RecentLogCount),
        };

        foreach (var entry in _properties)
        {
            members.Add(entry);
        }

        return members;
    }

    public override string ToString()
    {
        var id = Id ?? "<manager>";
        var state = string.Join(
            "/",
            new[] { LoadState, ActiveState, SubState }.Where(value => !string.IsNullOrWhiteSpace(value)));

        return string.IsNullOrWhiteSpace(state) ? id : $"{id} ({state})";
    }

    private object? GetValue(string name) => _properties.TryGetValue(name, out var value) ? value : null;

    private string? GetString(string name) => GetValue(name) as string;

    private IReadOnlyList<string> GetStringList(string name)
    {
        return GetValue(name) switch
        {
            null => Array.Empty<string>(),
            IReadOnlyList<string> values => values,
            IEnumerable<string> values => values.ToArray(),
            string value when string.IsNullOrWhiteSpace(value) => Array.Empty<string>(),
            string value => [value],
            _ => Array.Empty<string>(),
        };
    }

    private Guid? GetGuid(string name)
    {
        return GetValue(name) switch
        {
            Guid guid => guid,
            string text when SystemdParsingUtilities.TryParseCompactGuid(text, out var guid) => guid,
            _ => null,
        };
    }

    private int? GetInt32(string name)
    {
        return GetValue(name) switch
        {
            int direct => direct,
            long direct when direct is >= int.MinValue and <= int.MaxValue => (int)direct,
            _ => null,
        };
    }

    private bool? GetBoolean(string name)
    {
        return GetValue(name) switch
        {
            bool direct => direct,
            _ => null,
        };
    }

    private StorageSize? GetStorageSize(string name)
    {
        return GetValue(name) switch
        {
            StorageSize direct => direct,
            _ => null,
        };
    }

    private DateTimeOffset? GetDateTimeOffset(string name)
    {
        return GetValue(name) switch
        {
            DateTimeOffset direct => direct,
            _ => null,
        };
    }

    private IReadOnlyList<SystemdJournalEntry> GetJournalEntryList(string name)
    {
        return GetValue(name) switch
        {
            null => Array.Empty<SystemdJournalEntry>(),
            IReadOnlyList<SystemdJournalEntry> entries => entries,
            IEnumerable<SystemdJournalEntry> entries => entries.ToArray(),
            _ => Array.Empty<SystemdJournalEntry>(),
        };
    }
}
