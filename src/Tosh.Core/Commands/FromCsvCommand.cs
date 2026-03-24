namespace Tosh.Core.Commands;

public sealed class FromCsvCommand : ShellCommand
{
    public FromCsvCommand()
        : base("from-csv", "Parses CSV text into projected row objects.", "from-csv [csv-text]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var parsed = ParsedCommandArguments.Parse(context.Arguments);
        var text = await StructuredTextInput.ReadAllTextAsync(
            context,
            parsed.Positionals,
            "from-csv expects CSV text from the pipeline or an explicit argument.");

        IReadOnlyList<string[]> records;

        try
        {
            records = CsvParser.Parse(text);
        }
        catch (Exception exception) when (exception is InvalidOperationException)
        {
            throw new InvalidOperationException($"Could not parse CSV input. {exception.Message}");
        }

        if (records.Count == 0)
        {
            yield return Array.Empty<ProjectedObject>();
            yield break;
        }

        var headers = NormalizeHeaders(records[0]);
        var rows = records
            .Skip(1)
            .Select(record => CreateRow(headers, record))
            .ToArray();

        yield return rows;
    }

    private static ProjectedObject CreateRow(IReadOnlyList<string> headers, IReadOnlyList<string> values)
    {
        var fields = headers
            .Select((header, index) => new ProjectedField(
                header,
                header,
                index < values.Count ? values[index] : string.Empty))
            .ToArray();

        return new ProjectedObject(fields);
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
}
