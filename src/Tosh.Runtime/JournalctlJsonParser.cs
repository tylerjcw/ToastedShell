using System.Text;
using System.Text.Json;

namespace Tosh.Runtime;

public static class JournalctlJsonParser
{
    public static SystemdJournalEntry ParseLine(string jsonLine)
    {
        if (string.IsNullOrWhiteSpace(jsonLine))
        {
            throw new InvalidOperationException("The journal entry JSON line was empty.");
        }

        using var document = JsonDocument.Parse(jsonLine);

        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("Expected a journal entry JSON object.");
        }

        var fields = document.RootElement
            .EnumerateObject()
            .Select(property => new KeyValuePair<string, object?>(
                property.Name,
                SystemdParsingUtilities.ConvertJournalJsonValue(property.Name, property.Value)))
            .ToArray();

        return new SystemdJournalEntry(fields);
    }

    public static IReadOnlyList<SystemdJournalEntry> ParseMany(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<SystemdJournalEntry>();
        }

        var entries = new List<SystemdJournalEntry>();
        var bytes = Encoding.UTF8.GetBytes(json);
        var reader = new Utf8JsonReader(
            bytes,
            new JsonReaderOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowMultipleValues = true,
            });

        while (reader.Read())
        {
            if (reader.TokenType is JsonTokenType.Comment or JsonTokenType.None)
            {
                continue;
            }

            using var document = JsonDocument.ParseValue(ref reader);

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var fields = document.RootElement
                .EnumerateObject()
                .Select(property => new KeyValuePair<string, object?>(
                    property.Name,
                    SystemdParsingUtilities.ConvertJournalJsonValue(property.Name, property.Value)))
                .ToArray();

            entries.Add(new SystemdJournalEntry(fields));
        }

        return entries;
    }
}
