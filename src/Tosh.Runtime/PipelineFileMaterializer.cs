using System.Collections;
using System.Text;
using System.Text.Json;

namespace Tosh.Runtime;

public static class PipelineFileMaterializer
{
    public static async Task<FileSystemEntry> MaterializeAsync(
        string format,
        IReadOnlyList<object?> values,
        CancellationToken cancellationToken = default)
    {
        var normalizedFormat = NormalizeFormat(format);
        var extension = GetExtension(normalizedFormat);
        var path = Path.Combine(Path.GetTempPath(), $"tosh-materialized-{Guid.NewGuid():N}{extension}");
        var content = normalizedFormat switch
        {
            "json" => SerializeJson(values),
            "csv" => SerializeCsv(values),
            _ => SerializeText(values),
        };

        await File.WriteAllTextAsync(path, content, Encoding.UTF8, cancellationToken);
        return FileSystemEntry.From(new FileInfo(path), preferLongDisplay: true);
    }

    public static string NormalizeFormat(string? format)
    {
        if (string.IsNullOrWhiteSpace(format))
        {
            return "text";
        }

        return format.Trim().ToLowerInvariant() switch
        {
            "json" => "json",
            "csv" => "csv",
            _ => "text",
        };
    }

    public static string SerializeText(IReadOnlyList<object?> values)
    {
        if (values.Count == 0)
        {
            return string.Empty;
        }

        return string.Join(
                   Environment.NewLine,
                   values.Select(value => value is ShellTextLine line ? line.Text : ExternalTextSerializer.Serialize(value)))
               + Environment.NewLine;
    }

    public static string SerializeJson(IReadOnlyList<object?> values)
    {
        if (values.Count == 0)
        {
            return "null";
        }

        var normalized = values.Count == 1
            ? ShellDataSerializer.Normalize(values[0])
            : values.Select(ShellDataSerializer.Normalize).ToArray();

        return JsonSerializer.Serialize(
            normalized,
            new JsonSerializerOptions
            {
                WriteIndented = true,
            });
    }

    public static string SerializeCsv(IReadOnlyList<object?> values)
    {
        if (values.Count == 1 &&
            values[0] is IEnumerable enumerable &&
            values[0] is not string &&
            !ShellRecordUtilities.IsRecordLike(values[0]) &&
            values[0] is not IDictionary)
        {
            values = enumerable.Cast<object?>().ToArray();
        }

        if (values.Count == 0)
        {
            return string.Empty;
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
                headers.Select(header => Escape(SerializeCsvCell(row.TryGetValue(header, out var value) ? value : null)))));
        }

        return builder.ToString().TrimEnd();
    }

    private static string SerializeCsvCell(object? value)
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

    private static string GetExtension(string format)
    {
        return format switch
        {
            "json" => ".json",
            "csv" => ".csv",
            _ => ".txt",
        };
    }
}
