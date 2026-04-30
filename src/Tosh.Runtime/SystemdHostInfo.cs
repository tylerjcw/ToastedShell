namespace Tosh.Runtime;

public sealed class SystemdHostInfo : IShellRecordObject
{
    private readonly Dictionary<string, object?> _properties;

    public SystemdHostInfo(IEnumerable<KeyValuePair<string, object?>> properties)
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

    public string ShellTypeName => "SystemdHostInfo";

    public IReadOnlyDictionary<string, object?> Properties => _properties;

    public string? Hostname => GetString("Hostname");

    public string? StaticHostname => GetString("StaticHostname");

    public string? PrettyHostname => GetString("PrettyHostname");

    public string? DefaultHostname => GetString("DefaultHostname");

    public string? HostnameSource => GetString("HostnameSource");

    public string? IconName => GetString("IconName");

    public string? Chassis => GetString("Chassis");

    public string? Deployment => GetString("Deployment");

    public string? Location => GetString("Location");

    public string? KernelName => GetString("KernelName");

    public string? KernelRelease => GetString("KernelRelease");

    public string? KernelVersion => GetString("KernelVersion");

    public string? OperatingSystemPrettyName => GetString("OperatingSystemPrettyName");

    public string? OperatingSystemFancyName => GetString("OperatingSystemFancyName");

    public Uri? OperatingSystemHomeUrl => GetUri("OperatingSystemHomeURL");

    public string? HardwareVendor => GetString("HardwareVendor");

    public string? HardwareModel => GetString("HardwareModel");

    public string? HardwareVersion => GetString("HardwareVersion");

    public string? HardwareSerial => GetString("HardwareSerial");

    public string? FirmwareVendor => GetString("FirmwareVendor");

    public string? FirmwareVersion => GetString("FirmwareVersion");

    public DateTimeOffset? FirmwareDate => GetDateTimeOffset("FirmwareDate");

    public Guid? MachineId => GetGuid("MachineID");

    public Guid? BootId => GetGuid("BootID");

    public Guid? ProductUuid => GetGuid("ProductUUID");

    public long? VSockCid => GetInt64("VSockCID");

    public IReadOnlyList<string> OperatingSystemReleaseData => GetStringList("OperatingSystemReleaseData");

    public string DisplayHostname =>
        PrettyHostname ??
        StaticHostname ??
        Hostname ??
        DefaultHostname ??
        "<host>";

    public string? OperatingSystem => OperatingSystemPrettyName ?? OperatingSystemFancyName;

    public string Kernel =>
        string.Join(
            " ",
            new[] { KernelName, KernelRelease }
                .Where(value => !string.IsNullOrWhiteSpace(value)));

    public int PropertyCount => _properties.Count;

    public bool TryGetMember(string name, out object? value, bool includeHidden = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        value = name switch
        {
            nameof(DisplayHostname) => DisplayHostname,
            nameof(OperatingSystem) => OperatingSystem,
            nameof(Kernel) => Kernel,
            nameof(PropertyCount) => PropertyCount,
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
            new(nameof(DisplayHostname), DisplayHostname),
            new(nameof(OperatingSystem), OperatingSystem),
            new(nameof(Kernel), Kernel),
            new(nameof(PropertyCount), PropertyCount),
        };

        foreach (var entry in _properties)
        {
            members.Add(entry);
        }

        return members;
    }

    public override string ToString()
    {
        return string.IsNullOrWhiteSpace(OperatingSystem)
            ? DisplayHostname
            : $"{DisplayHostname} ({OperatingSystem})";
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

    private Uri? GetUri(string name)
    {
        return GetValue(name) switch
        {
            Uri uri => uri,
            string text when Uri.TryCreate(text, UriKind.Absolute, out var uri) => uri,
            _ => null,
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

    private DateTimeOffset? GetDateTimeOffset(string name)
    {
        return GetValue(name) switch
        {
            DateTimeOffset direct => direct,
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
}
