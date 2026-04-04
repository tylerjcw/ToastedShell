using System.Text;
using System.Text.Json;

namespace Tosh.Core;

internal static class HistoryFileStore
{
    public static IReadOnlyList<CommandHistoryEntry> Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
        {
            return Array.Empty<CommandHistoryEntry>();
        }

        var entries = new List<CommandHistoryEntry>();

        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                var serialized = JsonSerializer.Deserialize<SerializedHistoryEntry>(line, JsonOptions);

                if (serialized is null || string.IsNullOrWhiteSpace(serialized.Text))
                {
                    continue;
                }

                var id = serialized.Id ?? (entries.Count == 0 ? 1 : entries[^1].Id + 1);
                entries.Add(new CommandHistoryEntry(id, serialized.Text, serialized.Timestamp));
            }
            catch (JsonException)
            {
                continue;
            }
        }

        return entries;
    }

    public static void Save(string path, IReadOnlyList<CommandHistoryEntry> entries)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(entries);

        var directory = Path.GetDirectoryName(path);

        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        using (var writer = new StreamWriter(tempPath, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
        {
            foreach (var entry in entries)
            {
                var line = JsonSerializer.Serialize(new SerializedHistoryEntry(entry.Id, entry.Text, entry.Timestamp), JsonOptions);
                writer.WriteLine(line);
            }
        }

        File.Move(tempPath, path, overwrite: true);
    }

    private sealed record SerializedHistoryEntry(long? Id, string Text, DateTimeOffset Timestamp);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}
