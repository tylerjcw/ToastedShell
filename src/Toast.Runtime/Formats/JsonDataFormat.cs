using System.Text.Json;

namespace Tosh.Runtime.Formats;

public sealed class JsonDataFormat : IDataFormat
{
    public string Name => "json";
    public IReadOnlyList<string> Aliases => Array.Empty<string>();
    public string Description => "JavaScript Object Notation";

    public async IAsyncEnumerable<object?> DeserializeAsync(string text, IReadOnlyList<object?> arguments)
    {
        await Task.CompletedTask;

        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(text);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"Could not parse JSON input. {exception.Message}");
        }

        using (document)
        {
            yield return JsonValueConverter.Convert(document.RootElement);
        }
    }

    public async IAsyncEnumerable<object?> SerializeAsync(IReadOnlyList<object?> values, IReadOnlyList<object?> arguments)
    {
        await Task.CompletedTask;

        var args = ParsedCommandArguments.Parse(arguments);
        var normalized = values.Count == 1
            ? ShellDataSerializer.Normalize(values[0])
            : values.Select(ShellDataSerializer.Normalize).ToArray();

        string json;

        try
        {
            json = JsonSerializer.Serialize(
                normalized,
                args.HasFlag("c", "compact") ? ToshJson.Compact : ToshJson.Indented);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"Could not serialize value to JSON. {exception.Message}");
        }
        catch (NotSupportedException exception)
        {
            throw new InvalidOperationException($"Value cannot be represented as JSON. {exception.Message}");
        }

        yield return new ShellTextLine(json);
    }
}
