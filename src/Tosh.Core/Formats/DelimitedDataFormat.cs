using System.Collections;
using System.Text;
using System.Text.Json;

namespace Tosh.Core.Formats;

public sealed class DelimitedDataFormat : IDataFormat
{
    private readonly char _defaultDelimiter;

    public DelimitedDataFormat(string name, char defaultDelimiter, IReadOnlyList<string>? aliases = null)
    {
        Name = name;
        _defaultDelimiter = defaultDelimiter;
        Aliases = aliases ?? Array.Empty<string>();
    }

    public string Name { get; }
    public IReadOnlyList<string> Aliases { get; }
    public string Description => $"Delimited text (default separator: {DescribeDelimiter(_defaultDelimiter)})";

    public async IAsyncEnumerable<object?> DeserializeAsync(string text, IReadOnlyList<object?> arguments)
    {
        await Task.CompletedTask;

        var delimiter = ResolveDelimiter(arguments);
        IReadOnlyList<string[]> records;

        try
        {
            records = DelimitedParser.Parse(text, delimiter);
        }
        catch (Exception exception) when (exception is InvalidOperationException)
        {
            throw new InvalidOperationException($"Could not parse delimited input. {exception.Message}");
        }

        if (records.Count == 0)
        {
            yield return Array.Empty<System.Dynamic.ExpandoObject>();
            yield break;
        }

        var headers = NormalizeHeaders(records[0]);
        var rows = records
            .Skip(1)
            .Select(record => CreateRow(headers, record))
            .ToArray();

        yield return rows;
    }

    public async IAsyncEnumerable<object?> SerializeAsync(IReadOnlyList<object?> values, IReadOnlyList<object?> arguments)
    {
        await Task.CompletedTask;

        var delimiter = ResolveDelimiter(arguments);

        var unwrapped = values;

        if (values.Count == 1 &&
            values[0] is IEnumerable enumerable &&
            values[0] is not string &&
            !ShellRecordUtilities.IsRecordLike(values[0]) &&
            values[0] is not IDictionary)
        {
            unwrapped = enumerable.Cast<object?>().ToArray();
        }

        if (unwrapped.Count == 0)
        {
            yield return new ShellTextLine(string.Empty);
            yield break;
        }

        var rows = unwrapped.Select(ShellDataSerializer.NormalizeRow).ToArray();
        var headers = rows
            .SelectMany(row => row.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var builder = new StringBuilder();
        builder.AppendLine(string.Join(delimiter, headers.Select(h => Escape(h, delimiter))));

        foreach (var row in rows)
        {
            builder.AppendLine(string.Join(
                delimiter,
                headers.Select(header => Escape(SerializeCell(row.TryGetValue(header, out var value) ? value : null), delimiter))));
        }

        yield return new ShellTextLine(builder.ToString().TrimEnd());
    }

    private char ResolveDelimiter(IReadOnlyList<object?> arguments)
    {
        for (var i = 0; i < arguments.Count; i++)
        {
            var text = arguments[i]?.ToString();

            if (text is null)
            {
                continue;
            }

            if (text is "-d" or "--sep" or "--separator" or "--delimiter" && i + 1 < arguments.Count)
            {
                var sepText = arguments[i + 1]?.ToString() ?? string.Empty;

                return sepText switch
                {
                    "\\t" or "tab" => '\t',
                    _ when sepText.Length == 1 => sepText[0],
                    _ => throw new InvalidOperationException($"Separator must be a single character. Got: '{sepText}'."),
                };
            }
        }

        return _defaultDelimiter;
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

    private static string Escape(string text, char delimiter)
    {
        if (!text.Contains(delimiter) && !text.Contains('"') && !text.Contains('\n') && !text.Contains('\r'))
        {
            return text;
        }

        return "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    private static System.Dynamic.ExpandoObject CreateRow(IReadOnlyList<string> headers, IReadOnlyList<string> values)
    {
        return ShellRecordUtilities.CreateExpando(headers.Select((header, index) =>
            new KeyValuePair<string, object?>(
                header,
                index < values.Count ? values[index] : string.Empty)));
    }

    private static IReadOnlyList<string> NormalizeHeaders(IReadOnlyList<string> headers)
    {
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var normalized = new string[headers.Count];

        for (var index = 0; index < headers.Count; index++)
        {
            var baseName = string.IsNullOrWhiteSpace(headers[index])
                ? $"Column{index + 1}"
                : headers[index].Trim();

            if (!seen.TryAdd(baseName, 1))
            {
                seen[baseName]++;
                baseName = $"{baseName}_{seen[baseName]}";
            }

            normalized[index] = baseName;
        }

        return normalized;
    }

    private static string DescribeDelimiter(char delimiter)
    {
        return delimiter switch
        {
            ',' => "comma",
            '\t' => "tab",
            '|' => "pipe",
            ';' => "semicolon",
            _ => $"'{delimiter}'",
        };
    }
}
