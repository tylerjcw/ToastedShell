using System.Text;
using System.Text.Json;

namespace Tosh.Core;

internal static class DirectoryStackFileStore
{
    public static DirectoryStackState Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
        {
            return new DirectoryStackState([], 0);
        }

        try
        {
            var json = File.ReadAllText(path);
            var state = JsonSerializer.Deserialize<SerializedState>(json, JsonOptions);

            if (state is null || state.Entries is null || state.Entries.Count == 0)
            {
                return new DirectoryStackState([], 0);
            }

            var valid = state.Entries.Where(Directory.Exists).ToArray();

            if (valid.Length == 0)
            {
                return new DirectoryStackState([], 0);
            }

            var index = Math.Clamp(state.Index, 0, valid.Length - 1);
            return new DirectoryStackState(valid, index);
        }
        catch (JsonException)
        {
            return new DirectoryStackState([], 0);
        }
        catch (IOException)
        {
            return new DirectoryStackState([], 0);
        }
    }

    public static void Save(string path, IReadOnlyList<string> entries, int index)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(entries);

        var directory = Path.GetDirectoryName(path);

        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var state = new SerializedState(entries.ToList(), index);
        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        var json = JsonSerializer.Serialize(state, JsonOptions);
        File.WriteAllText(tempPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(tempPath, path, overwrite: true);
    }

    private sealed record SerializedState(List<string> Entries, int Index);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
}

internal sealed record DirectoryStackState(IReadOnlyList<string> Entries, int Index);
