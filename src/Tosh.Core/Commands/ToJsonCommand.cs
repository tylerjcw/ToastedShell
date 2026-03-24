using System.Text.Json;

namespace Tosh.Core.Commands;

public sealed class ToJsonCommand : ShellCommand
{
    public ToJsonCommand()
        : base("to-json", "Serializes pipeline values into JSON text.", "to-json [-c|--compact] [value ...]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var parsed = ParsedCommandArguments.Parse(context.Arguments);
        var values = parsed.Positionals.Count > 0
            ? parsed.Positionals
            : await AsyncEnumerableExtensions.ToListAsync(context.Input, context.CancellationToken);

        if (values.Count == 0)
        {
            yield return new ShellTextLine("null");
            yield break;
        }

        var normalized = values.Count == 1
            ? ShellDataSerializer.Normalize(values[0])
            : values.Select(ShellDataSerializer.Normalize).ToArray();

        string json;

        try
        {
            json = JsonSerializer.Serialize(
                normalized,
                new JsonSerializerOptions
                {
                    WriteIndented = !parsed.HasFlag("c", "compact"),
                });
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
