namespace Tosh.Runtime.Formats;

public sealed class TomlDataFormat : IDataFormat
{
    public string Name => "toml";
    public IReadOnlyList<string> Aliases => Array.Empty<string>();
    public string Description => "Tom's Obvious Minimal Language";

    public async IAsyncEnumerable<object?> DeserializeAsync(string text, IReadOnlyList<object?> arguments)
    {
        await Task.CompletedTask;

        Dictionary<string, object?> table;

        try
        {
            table = TomlParser.Parse(text);
        }
        catch (Exception exception) when (exception is InvalidOperationException)
        {
            throw new InvalidOperationException($"Could not parse TOML input. {exception.Message}");
        }

        yield return ConvertTable(table);
    }

    public async IAsyncEnumerable<object?> SerializeAsync(IReadOnlyList<object?> values, IReadOnlyList<object?> arguments)
    {
        await Task.CompletedTask;

        var args = ParsedCommandArguments.Parse(arguments);
        var compact = args.HasFlag("c", "compact");

        // `TOAST-0092`. TOML serialises the value directly rather than through `Normalize`, so
        // `--typed` has to normalise first — otherwise the flag was accepted and silently did
        // nothing, which is the failure mode the whole "round-trips or refuses" rule exists to
        // prevent.
        var typed = args.HasFlag("typed");
        var single = values.Count == 1 ? values[0] : values;
        var value = typed ? ShellDataSerializer.Normalize(single, typed: true) : single;

        string toml;

        try
        {
            toml = TomlParser.Serialize(value, indent: !compact);
        }
        catch (Exception exception) when (exception is InvalidOperationException)
        {
            throw new InvalidOperationException($"Could not serialize to TOML. {exception.Message}");
        }

        yield return new ShellTextLine(toml);
    }

    private static object? ConvertTable(Dictionary<string, object?> table)
    {
        var fields = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        foreach (var (key, value) in table)
        {
            fields[key] = ConvertValue(value);
        }

        return ShellRecordUtilities.CreateExpando(fields);
    }

    private static object? ConvertValue(object? value)
    {
        return value switch
        {
            Dictionary<string, object?> dict => ConvertTable(dict),
            List<object?> list => list.Select(ConvertValue).ToArray(),
            _ => value,
        };
    }
}
