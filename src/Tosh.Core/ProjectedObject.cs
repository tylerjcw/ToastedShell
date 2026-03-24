namespace Tosh.Core;

public sealed class ProjectedObject
{
    private readonly Dictionary<string, ProjectedField> _lookup;

    public ProjectedObject(IReadOnlyList<ProjectedField> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        Fields = fields;
        _lookup = fields.ToDictionary(field => field.Name, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<ProjectedField> Fields { get; }

    public int Count => Fields.Count;

    public object? this[string name] => TryGetValue(name, out var value)
        ? value
        : throw new KeyNotFoundException($"Projected field '{name}' was not found.");

    public bool TryGetValue(string name, out object? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (_lookup.TryGetValue(name, out var field))
        {
            value = field.Value;
            return true;
        }

        value = null;
        return false;
    }
}
