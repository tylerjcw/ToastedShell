using System.Text.Json;

namespace Tosh.Tome.Workspace;

/// <summary>
/// Imports VS Code <c>.code-workspace</c> JSON files into our native
/// <see cref="Workspace"/> model. Only <c>folders</c> and a best-effort
/// translation of <c>files.exclude</c> keys are honoured; everything
/// else (settings, extensions, tasks) is ignored.
/// </summary>
internal static class CodeWorkspaceImporter
{
    public static Workspace Parse(string json, string sourceName)
    {
        var opts = new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json, opts); }
        catch (JsonException ex)
        {
            throw new WorkspaceParseException($"{sourceName}: invalid .code-workspace JSON: {ex.Message}");
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new WorkspaceParseException($"{sourceName}: top-level value must be an object");

            var baseDir = Path.GetDirectoryName(Path.GetFullPath(sourceName)) ?? Environment.CurrentDirectory;
            var folders = new List<WorkspaceFolder>();
            if (root.TryGetProperty("folders", out var foldersEl) && foldersEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in foldersEl.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.Object) continue;
                    if (!entry.TryGetProperty("path", out var pathEl) || pathEl.ValueKind != JsonValueKind.String) continue;
                    var raw = pathEl.GetString() ?? "";
                    if (raw.Length == 0) continue;
                    var resolved = Path.IsPathRooted(raw) ? raw : Path.GetFullPath(Path.Combine(baseDir, raw));
                    string? alias = null;
                    if (entry.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String)
                        alias = nameEl.GetString();
                    folders.Add(new WorkspaceFolder(resolved, alias));
                }
            }

            var exclude = ExtractExclude(root);
            var name = Path.GetFileNameWithoutExtension(sourceName);
            if (string.IsNullOrEmpty(name)) name = "imported";

            return new Workspace
            {
                Name = name,
                SourcePath = null, // set by WorkspaceFile.Load
                Folders = folders,
                Exclude = exclude,
                OpenFiles = Array.Empty<string>(),
                Layout = new WorkspaceLayout(),
            };
        }
    }

    /// <summary>
    /// Best-effort: pull plain segment names out of <c>files.exclude</c>
    /// keys of the form <c>**/segment</c> or <c>**/segment/**</c>. Keys
    /// with values that are <c>false</c> are skipped. More elaborate
    /// glob patterns are dropped silently.
    /// </summary>
    private static List<string> ExtractExclude(JsonElement root)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Scan(JsonElement obj)
        {
            if (obj.ValueKind != JsonValueKind.Object) return;
            foreach (var prop in obj.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.False) continue;
                var key = prop.Name;
                if (!key.StartsWith("**/", StringComparison.Ordinal)) continue;
                var rest = key.Substring(3);
                if (rest.EndsWith("/**", StringComparison.Ordinal)) rest = rest[..^3];
                if (rest.Length == 0 || rest.Contains('/') || rest.Contains('*')) continue;
                if (seen.Add(rest)) result.Add(rest);
            }
        }

        if (root.TryGetProperty("files.exclude", out var direct)) Scan(direct);
        if (root.TryGetProperty("settings", out var settings) && settings.ValueKind == JsonValueKind.Object)
        {
            if (settings.TryGetProperty("files.exclude", out var nested)) Scan(nested);
        }
        return result;
    }
}
