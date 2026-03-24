using System.Collections;
using System.Text;
using System.Text.Json;

namespace Tosh.Core.Commands;

public sealed class ToCsvCommand : ShellCommand
{
    public ToCsvCommand()
        : base("to-csv", "Serializes pipeline values into CSV text.", "to-csv [value ...]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var values = context.Arguments.Count > 0
            ? context.Arguments
            : await AsyncEnumerableExtensions.ToListAsync(context.Input, context.CancellationToken);

        if (values.Count == 1 &&
            values[0] is IEnumerable enumerable &&
            values[0] is not string &&
            values[0] is not ProjectedObject &&
            values[0] is not IDictionary)
        {
            values = enumerable.Cast<object?>().ToArray();
        }

        if (values.Count == 0)
        {
            yield return new ShellTextLine(string.Empty);
            yield break;
        }

        var rows = values.Select(ShellDataSerializer.NormalizeRow).ToArray();
        var headers = rows
            .SelectMany(row => row.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var builder = new StringBuilder();
        builder.AppendLine(string.Join(',', headers.Select(Escape)));

        foreach (var row in rows)
        {
            builder.AppendLine(string.Join(
                ',',
                headers.Select(header => Escape(SerializeCell(row.TryGetValue(header, out var value) ? value : null)))));
        }

        yield return new ShellTextLine(builder.ToString().TrimEnd());
    }

    private static string SerializeCell(object? value)
    {
        return value switch
        {
            null => string.Empty,
            string text => text,
            bool boolean => boolean ? "true" : "false",
            _ => value is DateTime or DateTimeOffset or TimeSpan or Guid or Uri || value.GetType().IsPrimitive || value is decimal
                ? ExternalTextSerializer.Serialize(value)
                : JsonSerializer.Serialize(value),
        };
    }

    private static string Escape(string text)
    {
        if (!text.Contains(',') && !text.Contains('"') && !text.Contains('\n') && !text.Contains('\r'))
        {
            return text;
        }

        return "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
}
