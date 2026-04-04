namespace Tosh.Core;

public sealed class SystemdJournalEntry : IShellRecordObject
{
    private readonly Dictionary<string, object?> _fields;

    public SystemdJournalEntry(IEnumerable<KeyValuePair<string, object?>> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);

        _fields = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, value) in fields)
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                _fields[name] = value;
            }
        }
    }

    public string ShellTypeName => "SystemdJournalEntry";

    public IReadOnlyDictionary<string, object?> Fields => _fields;

    public DateTimeOffset? Timestamp => GetDateTimeOffset("__REALTIME_TIMESTAMP");

    public object? MonotonicTimestamp => GetValue("__MONOTONIC_TIMESTAMP");

    public string? Cursor => GetString("__CURSOR");

    public long? SequenceNumber => GetInt64("__SEQNUM");

    public Guid? MachineId => GetGuid("_MACHINE_ID");

    public Guid? BootId => GetGuid("_BOOT_ID");

    public Guid? InvocationId => GetGuid("_SYSTEMD_INVOCATION_ID");

    public string? Hostname => GetString("_HOSTNAME");

    public int? Priority => GetInt32("PRIORITY");

    public string PriorityName => SystemdParsingUtilities.GetJournalPriorityName(Priority);

    public string? Message => GetString("MESSAGE");

    public string? Unit => GetString("_SYSTEMD_UNIT") ?? GetString("UNIT");

    public string? UserUnit => GetString("_SYSTEMD_USER_UNIT");

    public string? Identifier => GetString("SYSLOG_IDENTIFIER");

    public string? Comm => GetString("_COMM");

    public string? Exe => GetString("_EXE");

    public string? CommandLine => GetString("_CMDLINE");

    public int? ProcessId => GetInt32("_PID");

    public int? SyslogPid => GetInt32("SYSLOG_PID");

    public int? UserId => GetInt32("_UID");

    public int? GroupId => GetInt32("_GID");

    public int? Facility => GetInt32("SYSLOG_FACILITY");

    public string? Transport => GetString("_TRANSPORT");

    public string? RuntimeScope => GetString("_RUNTIME_SCOPE");

    public string? Tty => GetString("_TTY");

    public string? SourceFile => GetString("CODE_FILE");

    public int? SourceLine => GetInt32("CODE_LINE");

    public string? SourceFunction => GetString("CODE_FUNC");

    public DateTimeOffset? SourceTimestamp => GetDateTimeOffset("_SOURCE_REALTIME_TIMESTAMP");

    public object? SourceMonotonicTimestamp => GetValue("_SOURCE_MONOTONIC_TIMESTAMP");

    public string Source =>
        Unit ??
        UserUnit ??
        Identifier ??
        Comm ??
        Exe ??
        Hostname ??
        "<journal>";

    public bool TryGetMember(string name, out object? value, bool includeHidden = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        value = name switch
        {
            nameof(PriorityName) => PriorityName,
            nameof(Source) => Source,
            _ => null,
        };

        if (value is not null)
        {
            return true;
        }

        return _fields.TryGetValue(name, out value);
    }

    public bool TrySetMember(string name, object? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _fields[name] = value;
        return true;
    }

    public IReadOnlyList<KeyValuePair<string, object?>> GetMembers(bool includeHidden = false)
    {
        var members = new List<KeyValuePair<string, object?>>
        {
            new(nameof(Timestamp), Timestamp),
            new(nameof(PriorityName), PriorityName),
            new(nameof(Source), Source),
        };

        foreach (var entry in _fields)
        {
            members.Add(entry);
        }

        return members;
    }

    public override string ToString()
    {
        var source = Source;
        return string.IsNullOrWhiteSpace(Message) ? source : $"{source}: {Message}";
    }

    private object? GetValue(string name) => _fields.TryGetValue(name, out var value) ? value : null;

    private string? GetString(string name) => GetValue(name) as string;

    private int? GetInt32(string name)
    {
        return GetValue(name) switch
        {
            int direct => direct,
            long direct when direct is >= int.MinValue and <= int.MaxValue => (int)direct,
            _ => null,
        };
    }

    private long? GetInt64(string name)
    {
        return GetValue(name) switch
        {
            int direct => direct,
            long direct => direct,
            _ => null,
        };
    }

    private Guid? GetGuid(string name)
    {
        return GetValue(name) switch
        {
            Guid direct => direct,
            string text when SystemdParsingUtilities.TryParseCompactGuid(text, out var value) => value,
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
}
