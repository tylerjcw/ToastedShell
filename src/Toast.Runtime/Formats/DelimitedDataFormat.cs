using System.Collections;
using System.Text;
using System.Text.Json;

namespace Tosh.Runtime.Formats;

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

        // `TOAST-0028`. No rows means no items. This used to yield one empty array and
        // rely on a downstream stage expanding it away; under a rule where the producer
        // decides shape, an empty table is emptiness rather than a value that looks empty.
        if (records.Count == 0)
        {
            yield break;
        }

        var headers = NormalizeHeaders(records[0]);
        var dataRecords = records.Skip(1).ToArray();

        // Column types are inferred from the whole column, so the rows have to be
        // in hand first. They already were — the previous code materialized them
        // too — so this costs no streaming that was not already given up.
        var converters = HasRawFlag(arguments)
            ? null
            : DelimitedValueInference.InferColumns(headers.Count, dataRecords);

        // `TOAST-0028`. One row per item, not the whole table as one value.
        //
        // Yielding the table worked only because the consumer expanded a lone collection,
        // which meant `from csv | select name` was reaching into an `ExpandoObject[]` and
        // being rescued downstream. Under a rule where the producer decides, this command
        // *is* the producer of a sequence of rows, and it says so.
        //
        // It is also the honest shape on its own merits: a table is rows, and the previous
        // form gave up streaming for every consumer rather than only for the column-type
        // inference that genuinely needs the rows in hand.
        foreach (var record in dataRecords)
        {
            yield return CreateRow(headers, record, converters);
        }
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

        // `TOAST-0092`. A typed write round-trips or refuses; it never degrades quietly while
        // claiming to be typed. CSV has no nesting, so a value with a nested shape has nowhere
        // to put it and is refused rather than flattened into columns nobody can read back.
        var typed = ParsedCommandArguments.Parse(arguments).HasFlag("typed");
        var rows = unwrapped.Select(value => ShellDataSerializer.NormalizeRow(value, typed)).ToArray();

        if (typed)
        {
            foreach (var row in rows)
            {
                foreach (var cell in row)
                {
                    if (cell.Value is IDictionary<string, object?> or object?[] or IReadOnlyList<object?>)
                    {
                        throw ToshDiagnosticException.Create(new ToshDiagnostic(
                            Code: "tosh.runtime.typed_csv_nesting",
                            Title: $"'{cell.Key}' holds a nested value, which CSV cannot represent.",
                            Help: "write this value as json, toml, xml or ton, which nest; or project "
                                + "the flat fields you want before converting."));
                    }
                }
            }
        }
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

    /// <summary>
    /// Builds one record, applying <paramref name="converters"/> where a column was
    /// inferred as numeric or boolean (<c>TS-P2-27</c>).
    /// </summary>
    private static System.Dynamic.ExpandoObject CreateRow(
        IReadOnlyList<string> headers,
        IReadOnlyList<string> values,
        Func<string, object?>?[]? converters)
    {
        return ShellRecordUtilities.CreateExpando(headers.Select((header, index) =>
        {
            var cell = index < values.Count ? values[index] : string.Empty;
            var converter = converters is not null && index < converters.Length
                ? converters[index]
                : null;

            // A textual column keeps the empty string it always had; a typed
            // column cannot, so a gap there becomes null rather than a value that
            // would not compare with its neighbours.
            return new KeyValuePair<string, object?>(
                header,
                converter is null ? cell : converter(cell));
        }));
    }

    /// <summary>
    /// <c>--raw</c> turns inference off and returns every column as text, which is
    /// what the format literally contains.
    /// </summary>
    private static bool HasRawFlag(IReadOnlyList<object?> arguments)
    {
        foreach (var argument in arguments)
        {
            if (argument?.ToString() is "--raw" or "--no-infer")
            {
                return true;
            }
        }

        return false;
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
