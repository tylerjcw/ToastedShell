namespace Tosh.Runtime;

public sealed class SystemdPropertySet : IShellRecordObject
{
    private readonly Dictionary<string, object?> _properties;

    public SystemdPropertySet(IEnumerable<KeyValuePair<string, object?>> properties)
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

    public string ShellTypeName => "SystemdPropertySet";

    public IReadOnlyDictionary<string, object?> Properties => _properties;

    public object? Id => GetValue("Id") ?? GetValue("UID");

    public string? Name => GetString("Name");

    public string? User => GetString("User") ?? GetString("Name");

    public string? State => GetString("State");

    public string? Class => GetString("Class");

    public string? Type => GetString("Type");

    public string? Seat => GetString("Seat");

    public string? Service => GetString("Service");

    public object? Display => GetValue("Display");

    public object? ActiveSession => GetValue("ActiveSession");

    public DateTimeOffset? Timestamp => GetDateTimeOffset("Timestamp");

    public int PropertyCount => _properties.Count;

    public string Identity => Id?.ToString() ?? Name ?? User ?? "<property-set>";

    public bool TryGetMember(string name, out object? value, bool includeHidden = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        value = name switch
        {
            nameof(Identity) => Identity,
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
            new(nameof(Identity), Identity),
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
        return string.IsNullOrWhiteSpace(State)
            ? Identity
            : $"{Identity} ({State})";
    }

    private object? GetValue(string name) => _properties.TryGetValue(name, out var value) ? value : null;

    private string? GetString(string name) => GetValue(name) as string;

    private DateTimeOffset? GetDateTimeOffset(string name)
    {
        return GetValue(name) switch
        {
            DateTimeOffset direct => direct,
            _ => null,
        };
    }
}
