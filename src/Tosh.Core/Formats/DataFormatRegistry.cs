namespace Tosh.Core.Formats;

public sealed class DataFormatRegistry
{
    private readonly Dictionary<string, IDataFormat> _formats = new(StringComparer.OrdinalIgnoreCase);

    public void Register(IDataFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);
        _formats[format.Name] = format;

        foreach (var alias in format.Aliases)
        {
            _formats[alias] = format;
        }
    }

    public IDataFormat Resolve(string name)
    {
        if (_formats.TryGetValue(name, out var format))
        {
            return format;
        }

        var available = string.Join(", ", _formats.Values
            .Select(f => f.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase));

        throw new InvalidOperationException($"Unknown format '{name}'. Available formats: {available}.");
    }

    public IReadOnlyList<IDataFormat> GetAll()
    {
        return _formats.Values
            .Distinct()
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
