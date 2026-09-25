using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Tosh.DevCompanion.Memory;

public sealed class SqliteMemoryStore : IMemoryStore
{
    private static readonly JsonSerializerOptions LinkJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly SqliteConnection _db;
    private readonly string? _sharedDirectory;

    // The shared files as this store last read or wrote them, by file name; null until the
    // directory has been read once. A difference means something else changed them — a pull,
    // a branch switch, another process using the same database — and they are imported before
    // anything is written over them. It is kept through a failed read or write, so a failure
    // never turns the next read into a wholesale import over changes still waiting to be written.
    private Dictionary<string, string>? _sharedFiles;

    // Shared files that do not parse, by file name, with the reason; most often a merge left
    // conflict markers in one. They are neither imported nor written over until they parse
    // again, and every other file syncs as usual.
    private readonly Dictionary<string, string> _unreadableSharedFiles = new(StringComparer.Ordinal);

    // Why the shared directory could not be read, or written, the last time that was tried.
    // Nothing is written while it cannot be read.
    private string? _sharedReadProblem;
    private string? _sharedWriteProblem;

    private SqliteMemoryStore(SqliteConnection db, string? sharedDirectory)
    {
        _db = db;
        _sharedDirectory = sharedDirectory;
    }

    /// <param name="sharedDirectory">
    /// Where memories stored with <c>visibility = "shared"</c> are mirrored, a file each — the
    /// project's <c>.tosh/memories/</c> — or <see langword="null"/> to keep them in the database only.
    /// </param>
    public static async Task<SqliteMemoryStore> OpenAsync(
        string dbPath,
        string? sharedDirectory = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        var conn = new SqliteConnection($"Data Source={dbPath}");
        await conn.OpenAsync(ct);
        await ApplySchemaAsync(conn, ct);

        var store = new SqliteMemoryStore(conn, sharedDirectory);
        await store.RefreshSharedMemoriesAsync(ct);

        // A memory shared before there was a directory, or while it could not be written,
        // reaches it now rather than with the next unrelated change.
        await store.SyncSharedMemoriesAsync(ct);
        return store;
    }

    public string? SharedMemoryProblem =>
        _sharedReadProblem ?? _sharedWriteProblem ??
        (_unreadableSharedFiles.Count == 0
            ? null
            : string.Join("; ", _unreadableSharedFiles.Select(f =>
                $"{Path.Combine(_sharedDirectory!, f.Key)}: {f.Value}")));

    // ── IMemoryStore ──────────────────────────────────────────────────────────
    //
    // Each public operation first imports the shared directory if something else has changed
    // it, and each write ends with one sync of it, so a compound write (a content edit, a
    // batch) rewrites files once, after it has committed.

    public async Task<StoreResult> StoreAsync(StoreRequest req, CancellationToken ct = default)
    {
        await RefreshSharedMemoriesAsync(ct);
        var result = await StoreCoreAsync(req, ct);
        await SyncSharedMemoriesAsync(ct);
        return result;
    }

    private async Task<StoreResult> StoreCoreAsync(StoreRequest req, CancellationToken ct)
    {
        // Dedup-on-store via SHA-256 of the content body. Same body, not
        // tombstoned → bump accessed_at, return existing row with Deduped=true.
        var hash = ComputeContentHash(req.Content);
        var existing = await FindLiveByHashAsync(hash, ct);
        if (existing is not null)
        {
            // Asking to share a body that is already stored privately shares it. Otherwise the
            // dedup hands back the private row and the request to share silently disappears.
            if (req.Visibility == "shared" && existing.Visibility != "shared")
                await SetVisibilityAsync(existing.Id, "shared", ct);

            await BumpAccessAsync([existing.Id], ct);
            return new StoreResult((await GetAsync(existing.Id, ct))!, Deduped: true);
        }

        var id = Guid.CreateVersion7().ToString();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var tags = string.Join(',', req.Tags);
        var linksJson = SerializeLinks(req.Links);

        await using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO memories
                (id, content, summary, tags, category, source, scope, visibility,
                 session_id, created_at, accessed_at, access_count, is_deleted,
                 pinned, links, content_hash)
            VALUES
                ($id, $content, $summary, $tags, $category, $source, $scope, $visibility,
                 $session, $now, $now, 0, 0,
                 $pinned, $links, $hash)
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$content", req.Content);
        cmd.Parameters.AddWithValue("$summary", req.Summary);
        cmd.Parameters.AddWithValue("$tags", tags);
        cmd.Parameters.AddWithValue("$category", req.Category);
        cmd.Parameters.AddWithValue("$source", req.Source);
        cmd.Parameters.AddWithValue("$scope", req.Scope);
        cmd.Parameters.AddWithValue("$visibility", req.Visibility);
        cmd.Parameters.AddWithValue("$session", req.SessionId ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$now", now);
        cmd.Parameters.AddWithValue("$pinned", req.Pinned ? 1 : 0);
        cmd.Parameters.AddWithValue("$links", (object?)linksJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$hash", hash);
        await cmd.ExecuteNonQueryAsync(ct);

        return new StoreResult((await GetAsync(id, ct))!, Deduped: false);
    }

    public async Task<IReadOnlyList<StoreResult>> StoreBatchAsync(
        IReadOnlyList<StoreRequest> requests, CancellationToken ct = default)
    {
        await RefreshSharedMemoriesAsync(ct);
        var results = new List<StoreResult>(requests.Count);
        await using (var tx = (SqliteTransaction)await _db.BeginTransactionAsync(ct))
        {
            foreach (var req in requests)
                results.Add(await StoreCoreAsync(req, ct));
            await tx.CommitAsync(ct);
        }

        await SyncSharedMemoriesAsync(ct);
        return results;
    }

    public async Task<MemoryEntry> UpdateAsync(UpdateRequest req, CancellationToken ct = default)
    {
        await RefreshSharedMemoriesAsync(ct);
        var entry = await UpdateCoreAsync(req, ct);
        await SyncSharedMemoriesAsync(ct);
        return entry;
    }

    private async Task<MemoryEntry> UpdateCoreAsync(UpdateRequest req, CancellationToken ct)
    {
        var resolved = await ResolveIdAsync(req.Id, ct)
            ?? throw new InvalidOperationException($"No memory matches id '{req.Id}'.");
        var current = await GetAsync(resolved, ct)
            ?? throw new InvalidOperationException($"Memory '{resolved}' not found.");

        // Content edits are non-destructive: insert a new row, link old→new
        // via supersedes, then tombstone the old. Preserves audit history.
        if (req.Content is not null && req.Content != current.Content)
        {
            var migrated = await StoreCoreAsync(new StoreRequest(
                Content: req.Content,
                Summary: req.Summary ?? current.Summary,
                Category: current.Category,
                Source: current.Source,
                Scope: req.Scope ?? current.Scope,
                Visibility: req.Visibility ?? current.Visibility,
                Tags: req.Tags ?? current.TagList,
                SessionId: current.SessionId,
                Pinned: req.Pinned ?? current.Pinned,
                Links: req.Links ?? DeserializeLinks(current.LinksJson)), ct);

            await RelateCoreAsync(migrated.Entry.Id, current.Id, "supersedes", ct);
            await ForgetCoreAsync(new ForgetRequest(current.Id, Confirm: true,
                Reason: "superseded via update"), ct);
            return migrated.Entry;
        }

        // In-place metadata patch — single UPDATE.
        var sets = new List<string>();
        var pars = new Dictionary<string, object?>();
        if (req.Summary is not null) { sets.Add("summary = $summary"); pars["$summary"] = req.Summary; }
        if (req.Tags is not null) { sets.Add("tags = $tags"); pars["$tags"] = string.Join(',', req.Tags); }
        if (req.Scope is not null) { sets.Add("scope = $scope"); pars["$scope"] = req.Scope; }
        if (req.Visibility is not null) { sets.Add("visibility = $visibility"); pars["$visibility"] = req.Visibility; }
        if (req.Pinned is not null) { sets.Add("pinned = $pinned"); pars["$pinned"] = req.Pinned.Value ? 1 : 0; }
        if (req.Links is not null) { sets.Add("links = $links"); pars["$links"] = (object?)SerializeLinks(req.Links) ?? DBNull.Value; }

        if (sets.Count == 0) return current;

        await using var cmd = _db.CreateCommand();
        cmd.CommandText = $"UPDATE memories SET {string.Join(", ", sets)} WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", current.Id);
        foreach (var (k, v) in pars) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);

        return (await GetAsync(current.Id, ct))!;
    }

    public async Task<RecallResult> RecallAsync(RecallRequest req, CancellationToken ct = default)
    {
        await RefreshSharedMemoriesAsync(ct);
        var limit = Math.Clamp(req.Limit, 1, 50);
        var wantSnippet = req.Mode != "brief";

        await using var cmd = _db.CreateCommand();
        // snippet(): col=-1 = best column, 16 tokens of context. Pinned rows
        // get a fixed rank boost ahead of FTS5 rank (which is negative; smaller
        // is better — we subtract a constant for pinned).
        cmd.CommandText = """
            SELECT m.id, m.content, m.summary, m.tags, m.category, m.source,
                   m.scope, m.visibility, m.session_id,
                   m.created_at, m.accessed_at, m.access_count,
                   m.is_deleted, m.deleted_at, m.pinned, m.links,
                   rank AS relevance,
                   snippet(memories_fts, -1, '«', '»', '…', 16) AS excerpt
            FROM   memories_fts f
            JOIN   memories m ON m.rowid = f.rowid
            WHERE  memories_fts MATCH $query
              AND  m.is_deleted = 0
              AND  ($category IS NULL OR m.category = $category)
              AND  ($scope = 'all' OR m.scope = $scope)
            ORDER  BY (rank - (m.pinned * 5.0)), m.accessed_at DESC
            LIMIT  $limit
            """;
        cmd.Parameters.AddWithValue("$query", req.Query);
        cmd.Parameters.AddWithValue("$category", req.Category ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$scope", req.Scope);
        cmd.Parameters.AddWithValue("$limit", limit);

        var results = new List<ScoredMemory>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var entry = ReadEntry(reader);
            var score = reader.IsDBNull(16) ? 0.0 : -reader.GetDouble(16);
            var snippet = wantSnippet && !reader.IsDBNull(17) ? reader.GetString(17) : null;
            results.Add(new ScoredMemory(entry, Math.Round(score, 4), snippet));
        }

        if (results.Count > 0)
            await BumpAccessAsync(results.Select(r => r.Entry.Id).ToArray(), ct);

        if (req.Tags.Length > 0)
        {
            results = [..results.Where(r =>
                req.Tags.All(t => r.Entry.TagList.Contains(t, StringComparer.OrdinalIgnoreCase)))];
        }

        if (!string.IsNullOrEmpty(req.LinksPath))
        {
            results = [.. results.Where(r => HasLinkPath(r.Entry.LinksJson, req.LinksPath))];
        }

        // Brief mode drops content/links from the returned entry to keep tokens
        // down — done last so post-filters still see the full data.
        if (req.Mode == "brief")
        {
            results = [..results.Select(r => r with {
                Entry = r.Entry with { Content = string.Empty, LinksJson = null }
            })];
        }

        return new RecallResult(results, results.Count, req.Query);
    }

    public async Task<IReadOnlyList<MemoryEntry>> ListAsync(ListRequest req, CancellationToken ct = default)
    {
        await RefreshSharedMemoriesAsync(ct);
        var limit = Math.Clamp(req.Limit, 1, 200);
        var minAgeCutoff = req.MinAgeDays is int d
            ? DateTimeOffset.UtcNow.AddDays(-d).ToUnixTimeMilliseconds()
            : (long?)null;

        await using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            SELECT id, content, summary, tags, category, source,
                   scope, visibility, session_id,
                   created_at, accessed_at, access_count, is_deleted, deleted_at,
                   pinned, links
            FROM   memories
            WHERE  is_deleted = 0
              AND  ($category IS NULL OR category = $category)
              AND  ($scope = 'all' OR scope = $scope)
              AND  ($sinceSession IS NULL OR session_id >= $sinceSession)
              AND  ($minAge IS NULL OR accessed_at <= $minAge)
              AND  ($maxAccess IS NULL OR access_count <= $maxAccess)
            ORDER  BY pinned DESC, created_at DESC
            LIMIT  $limit
            """;
        cmd.Parameters.AddWithValue("$category", req.Category ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$scope", req.Scope);
        cmd.Parameters.AddWithValue("$sinceSession", req.SinceSession ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$minAge", (object?)minAgeCutoff ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$maxAccess", (object?)req.MaxAccessCount ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$limit", limit);

        var entries = new List<MemoryEntry>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            entries.Add(ReadEntry(reader));

        if (req.Tags.Length > 0)
        {
            entries = [..entries.Where(e =>
                req.Tags.All(t => e.TagList.Contains(t, StringComparer.OrdinalIgnoreCase)))];
        }

        if (!req.IncludeContent)
            entries = [.. entries.Select(e => e with { Content = string.Empty })];

        return entries;
    }

    public async Task<MemoryEntry?> GetAsync(string id, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            SELECT id, content, summary, tags, category, source,
                   scope, visibility, session_id,
                   created_at, accessed_at, access_count, is_deleted, deleted_at,
                   pinned, links
            FROM   memories
            WHERE  id = $id
            """;
        cmd.Parameters.AddWithValue("$id", id);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadEntry(reader) : null;
    }

    public async Task<string?> ResolveIdAsync(string idOrPrefix, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(idOrPrefix)) return null;

        // Exact match first — fast path for full UUIDs.
        await using (var exact = _db.CreateCommand())
        {
            exact.CommandText = "SELECT id FROM memories WHERE id = $id LIMIT 1";
            exact.Parameters.AddWithValue("$id", idOrPrefix);
            var hit = await exact.ExecuteScalarAsync(ct);
            if (hit is string s) return s;
        }

        // Need at least 4 chars to attempt a partial match — avoids accidental
        // catastrophic resolutions when someone passes a single character. A short id is
        // the end of an id; any other fragment may be its start.
        if (idOrPrefix.Length < 4) return null;

        await using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT id FROM memories WHERE id LIKE $prefix OR id LIKE $suffix LIMIT 2";
        cmd.Parameters.AddWithValue("$prefix", idOrPrefix + "%");
        cmd.Parameters.AddWithValue("$suffix", "%" + idOrPrefix);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        if (!await reader.ReadAsync(ct)) return null;
        var first = reader.GetString(0);
        if (await reader.ReadAsync(ct))
            throw new InvalidOperationException($"Ambiguous id prefix '{idOrPrefix}' — at least two matches.");
        return first;
    }

    public async Task<bool> ForgetAsync(ForgetRequest req, CancellationToken ct = default)
    {
        await RefreshSharedMemoriesAsync(ct);
        var deleted = await ForgetCoreAsync(req, ct);
        await SyncSharedMemoriesAsync(ct);
        return deleted;
    }

    private async Task<bool> ForgetCoreAsync(ForgetRequest req, CancellationToken ct)
    {
        var resolved = await ResolveIdAsync(req.Id, ct);
        if (resolved is null) return false;
        var entry = await GetAsync(resolved, ct);
        if (entry is null || entry.IsDeleted) return false;

        if (entry.Source == "user" && !req.Confirm)
            throw new InvalidOperationException(
                "User-sourced memories require confirm=true to delete.");

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            UPDATE memories
            SET is_deleted = 1, deleted_at = $now
            WHERE id = $id
            """;
        cmd.Parameters.AddWithValue("$now", now);
        cmd.Parameters.AddWithValue("$id", entry.Id);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task RelateAsync(string fromId, string toId, string relationship, CancellationToken ct = default)
    {
        await RefreshSharedMemoriesAsync(ct);
        await RelateCoreAsync(fromId, toId, relationship, ct);
        await SyncSharedMemoriesAsync(ct);
    }

    private async Task RelateCoreAsync(string fromId, string toId, string relationship, CancellationToken ct)
    {
        var resolvedFrom = await ResolveIdAsync(fromId, ct)
            ?? throw new InvalidOperationException($"Unknown from_id '{fromId}'.");
        var resolvedTo = await ResolveIdAsync(toId, ct)
            ?? throw new InvalidOperationException($"Unknown to_id '{toId}'.");

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO memory_relations (from_id, to_id, relationship, created_at)
            VALUES ($from, $to, $rel, $now)
            """;
        cmd.Parameters.AddWithValue("$from", resolvedFrom);
        cmd.Parameters.AddWithValue("$to", resolvedTo);
        cmd.Parameters.AddWithValue("$rel", relationship);
        cmd.Parameters.AddWithValue("$now", now);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<MemoryRelation>> GetRelationsAsync(string id, CancellationToken ct = default)
    {
        await RefreshSharedMemoriesAsync(ct);
        await using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            SELECT from_id, to_id, relationship, created_at
            FROM   memory_relations
            WHERE  from_id = $id OR to_id = $id
            ORDER  BY created_at DESC
            """;
        cmd.Parameters.AddWithValue("$id", id);

        var relations = new List<MemoryRelation>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            relations.Add(new MemoryRelation(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt64(3)));
        return relations;
    }

    public async Task<IReadOnlyList<TagCount>> GetTagsAsync(CancellationToken ct = default)
    {
        await RefreshSharedMemoriesAsync(ct);
        await using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT tags FROM memories WHERE is_deleted = 0 AND tags <> ''";
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            foreach (var t in reader.GetString(0).Split(',',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                counts.TryGetValue(t, out var n);
                counts[t] = n + 1;
            }
        }
        return [..counts
            .Select(kv => new TagCount(kv.Key, kv.Value))
            .OrderByDescending(t => t.Count)
            .ThenBy(t => t.Tag, StringComparer.Ordinal)];
    }

    public async Task<IReadOnlyList<MemoryEntry>> GetPinnedAsync(string scope, int limit, CancellationToken ct = default)
    {
        await RefreshSharedMemoriesAsync(ct);
        await using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            SELECT id, content, summary, tags, category, source,
                   scope, visibility, session_id,
                   created_at, accessed_at, access_count, is_deleted, deleted_at,
                   pinned, links
            FROM   memories
            WHERE  is_deleted = 0 AND pinned = 1
              AND  ($scope = 'all' OR scope = $scope)
            ORDER  BY created_at DESC
            LIMIT  $limit
            """;
        cmd.Parameters.AddWithValue("$scope", scope);
        cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 50));

        var entries = new List<MemoryEntry>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            entries.Add(ReadEntry(reader) with { Content = string.Empty });
        return entries;
    }

    public async Task<MemoryGraph> GetGraphAsync(GraphRequest req, CancellationToken ct = default)
    {
        await RefreshSharedMemoriesAsync(ct);
        var depth = Math.Clamp(req.Depth, 0, 3);

        // Resolve seeds (accept full id, short_id, or unambiguous prefix).
        var frontier = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in req.Seeds)
        {
            var resolved = await ResolveIdAsync(s, ct);
            if (resolved is not null) frontier.Add(resolved);
        }

        var visited = new HashSet<string>(frontier, StringComparer.Ordinal);
        var edges = new List<MemoryRelation>();
        var edgeKeys = new HashSet<string>(StringComparer.Ordinal);

        for (var hop = 0; hop < depth && frontier.Count > 0; hop++)
        {
            var next = new HashSet<string>(StringComparer.Ordinal);
            var idList = string.Join(',', frontier.Select(i => $"'{i.Replace("'", "''")}'"));
            var relFilter = req.Relationship is null
                ? string.Empty
                : "AND relationship = $rel";

            await using var cmd = _db.CreateCommand();
            cmd.CommandText = $"""
                SELECT from_id, to_id, relationship, created_at
                FROM   memory_relations
                WHERE  (from_id IN ({idList}) OR to_id IN ({idList}))
                       {relFilter}
                """;
            if (req.Relationship is not null)
                cmd.Parameters.AddWithValue("$rel", req.Relationship);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var from = reader.GetString(0);
                var to = reader.GetString(1);
                var rel = reader.GetString(2);
                var key = $"{from}|{to}|{rel}";
                if (!edgeKeys.Add(key)) continue;
                edges.Add(new MemoryRelation(from, to, rel, reader.GetInt64(3)));
                if (visited.Add(from)) next.Add(from);
                if (visited.Add(to)) next.Add(to);
            }
            frontier = next;
        }

        var nodes = new List<MemoryEntry>(visited.Count);
        foreach (var id in visited)
        {
            var entry = await GetAsync(id, ct);
            if (entry is null) continue;
            nodes.Add(req.IncludeContent ? entry : entry with { Content = string.Empty });
        }
        return new MemoryGraph(nodes, edges);
    }

    public void Dispose() => _db.Dispose();

    // ── Shared directory ──────────────────────────────────────────────────────

    /// <summary>
    /// Imports the shared directory if anything in it has changed since this store last read or
    /// wrote it. The files are what git versions, so their memories win, and a memory whose file
    /// has gone is kept but made private.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every public operation starts here, so a memory that a pull, a revert or a branch switch
    /// brought in or took away while the server was running is seen by the next call — and is
    /// never written back out of, or into, the directory by a store that had not noticed.
    /// </para>
    /// <para>
    /// "Gone" means the directory held its file at the last sync (<c>shared_file_ids</c>) and
    /// holds it no more. A shared memory that never had a file is not demoted: it was shared
    /// before there was a directory, or while it could not be written, and the next sync adds it.
    /// </para>
    /// </remarks>
    private async Task RefreshSharedMemoriesAsync(CancellationToken ct)
    {
        if (_sharedDirectory is null) return;

        Dictionary<string, string> files;
        try
        {
            files = await ReadSharedFilesAsync(_sharedDirectory, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Without knowing what the directory holds nothing can be written over it; the next
            // call reads it again.
            _sharedReadProblem = $"could not read {_sharedDirectory}: {ex.Message}";
            await Console.Error.WriteLineAsync($"tosh-devcompanion: {_sharedReadProblem}");
            return;
        }

        _sharedReadProblem = null;
        if (_sharedFiles is not null && SameFiles(files, _sharedFiles)) return;

        _sharedFiles = files;
        _unreadableSharedFiles.Clear();

        var documents = new List<SharedMemoryDocument>(files.Count);
        foreach (var (name, text) in files.OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            try
            {
                var document = SharedMemoryFile.Parse(text);
                var expected = SharedMemoryFile.FileName(document.Memory.Id);
                if (expected != name)
                    throw new FormatException($"it holds memory '{document.Memory.Id}', whose file is {expected}");

                documents.Add(document);
            }
            catch (FormatException ex)
            {
                _unreadableSharedFiles[name] = ex.Message;
                await Console.Error.WriteLineAsync(
                    $"tosh-devcompanion: not syncing {Path.Combine(_sharedDirectory, name)}: {ex.Message}");
            }
        }

        // A file that does not parse still lists its memory, so the memory is not demoted.
        var listed = documents.Select(d => d.Memory.Id)
            .Concat(_unreadableSharedFiles.Keys.Select(Path.GetFileNameWithoutExtension).OfType<string>())
            .ToHashSet(StringComparer.Ordinal);

        await using var tx = (SqliteTransaction)await _db.BeginTransactionAsync(ct);

        foreach (var document in documents)
            await UpsertSharedAsync(document.Memory, ct);

        foreach (var id in await ReadSharedFileIdsAsync(ct))
        {
            if (!listed.Contains(id))
                await DemoteToPrivateAsync(id, ct);
        }

        foreach (var relation in documents.SelectMany(d => d.Relations))
            await InsertRelationIfPresentAsync(relation, ct);

        await RecordSharedFileIdsAsync(listed, ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>
    /// Brings the shared directory in line with the database: a file for every shared memory,
    /// rewritten only when what it would say has changed, and none for a memory that is no longer shared.
    /// </summary>
    private async Task SyncSharedMemoriesAsync(CancellationToken ct)
    {
        if (_sharedDirectory is null || _sharedFiles is null || _sharedReadProblem is not null) return;

        var memories = await ReadSharedMemoriesAsync(ct);
        var relations = (await ReadSharedRelationsAsync(ct)).ToLookup(r => r.FromId, StringComparer.Ordinal);
        var wanted = memories.ToDictionary(
            m => SharedMemoryFile.FileName(m.Id),
            m => SharedMemoryFile.Render(m, relations[m.Id]),
            StringComparer.Ordinal);

        try
        {
            foreach (var (name, text) in wanted)
            {
                // A file that does not parse is left for whoever is resolving it, and one that
                // differs only in line endings (a checkout with autocrlf) is left as it is.
                if (_unreadableSharedFiles.ContainsKey(name)) continue;
                if (_sharedFiles.TryGetValue(name, out var current) && current.ReplaceLineEndings("\n") == text) continue;

                Directory.CreateDirectory(_sharedDirectory);
                var path = Path.Combine(_sharedDirectory, name);
                var temp = path + ".tmp";
                await File.WriteAllTextAsync(temp, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), ct);
                File.Move(temp, path, overwrite: true);
                _sharedFiles[name] = text;
            }

            foreach (var name in _sharedFiles.Keys.Where(n => !wanted.ContainsKey(n)).ToList())
            {
                if (_unreadableSharedFiles.ContainsKey(name)) continue;

                File.Delete(Path.Combine(_sharedDirectory, name));
                _sharedFiles.Remove(name);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The database already holds the change. A directory that cannot be written is
            // reported rather than allowed to fail the call that made the change, and the next
            // write tries again. The ids are not recorded, so nothing is demoted for a file that
            // never got written.
            _sharedWriteProblem = $"could not write {_sharedDirectory}: {ex.Message}";
            await Console.Error.WriteLineAsync($"tosh-devcompanion: {_sharedWriteProblem}");
            return;
        }

        _sharedWriteProblem = null;

        var listed = wanted.Keys.Concat(_unreadableSharedFiles.Keys)
            .Select(Path.GetFileNameWithoutExtension)
            .OfType<string>();

        await using var tx = (SqliteTransaction)await _db.BeginTransactionAsync(ct);
        await RecordSharedFileIdsAsync(listed, ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>Every <c>*.toml</c> file directly in the directory, by file name; none when it does not exist.</summary>
    private static async Task<Dictionary<string, string>> ReadSharedFilesAsync(string directory, CancellationToken ct)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!Directory.Exists(directory)) return files;

        foreach (var path in Directory.EnumerateFiles(directory, "*.toml"))
            files[Path.GetFileName(path)] = await File.ReadAllTextAsync(path, ct);

        return files;
    }

    private static bool SameFiles(Dictionary<string, string> a, Dictionary<string, string> b) =>
        a.Count == b.Count &&
        a.All(f => b.TryGetValue(f.Key, out var text) && string.Equals(text, f.Value, StringComparison.Ordinal));

    private async Task<List<SharedMemory>> ReadSharedMemoriesAsync(CancellationToken ct)
    {
        await using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            SELECT id, content, summary, tags, category, source,
                   scope, visibility, session_id,
                   created_at, accessed_at, access_count, is_deleted, deleted_at,
                   pinned, links
            FROM   memories
            WHERE  visibility = 'shared'
            """;

        var memories = new List<SharedMemory>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var e = ReadEntry(reader);
            memories.Add(new SharedMemory(
                Id: e.Id,
                CreatedAt: e.CreatedAt,
                Category: e.Category,
                Scope: e.Scope,
                Source: e.Source,
                Pinned: e.Pinned,
                SessionId: e.SessionId,
                Tags: e.TagList,
                Summary: e.Summary,
                Content: e.Content,
                Links: DeserializeLinks(e.LinksJson),
                DeletedAt: e.IsDeleted ? e.DeletedAt ?? e.CreatedAt : null));
        }

        return memories;
    }

    /// <summary>Relations whose two ends are both shared; a link to a private memory stays local.</summary>
    private async Task<List<MemoryRelation>> ReadSharedRelationsAsync(CancellationToken ct)
    {
        await using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            SELECT r.from_id, r.to_id, r.relationship, r.created_at
            FROM   memory_relations r
            JOIN   memories f ON f.id = r.from_id AND f.visibility = 'shared'
            JOIN   memories t ON t.id = r.to_id   AND t.visibility = 'shared'
            """;

        var relations = new List<MemoryRelation>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            relations.Add(new MemoryRelation(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt64(3)));
        }

        return relations;
    }

    /// <summary>The ids the shared directory held files for at the last import or export.</summary>
    private async Task<List<string>> ReadSharedFileIdsAsync(CancellationToken ct)
    {
        await using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT id FROM shared_file_ids";

        var ids = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            ids.Add(reader.GetString(0));
        return ids;
    }

    private async Task RecordSharedFileIdsAsync(IEnumerable<string> ids, CancellationToken ct)
    {
        await using (var clear = _db.CreateCommand())
        {
            clear.CommandText = "DELETE FROM shared_file_ids";
            await clear.ExecuteNonQueryAsync(ct);
        }

        foreach (var id in ids)
        {
            await using var insert = _db.CreateCommand();
            insert.CommandText = "INSERT OR IGNORE INTO shared_file_ids (id) VALUES ($id)";
            insert.Parameters.AddWithValue("$id", id);
            await insert.ExecuteNonQueryAsync(ct);
        }
    }

    private async Task DemoteToPrivateAsync(string id, CancellationToken ct)
    {
        await using var cmd = _db.CreateCommand();
        cmd.CommandText = "UPDATE memories SET visibility = 'private' WHERE id = $id AND visibility = 'shared'";
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Inserts or refreshes one memory from its shared file. Local access statistics are kept,
    /// and a row that already matches is left untouched so the search index is not rebuilt for it.
    /// </summary>
    private async Task UpsertSharedAsync(SharedMemory m, CancellationToken ct)
    {
        await using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO memories
                (id, content, summary, tags, category, source, scope, visibility,
                 session_id, created_at, accessed_at, access_count, is_deleted, deleted_at,
                 pinned, links, content_hash)
            VALUES
                ($id, $content, $summary, $tags, $category, $source, $scope, 'shared',
                 $session, $created, $created, 0, $isDeleted, $deletedAt,
                 $pinned, $links, $hash)
            ON CONFLICT(id) DO UPDATE SET
                content    = excluded.content,
                summary    = excluded.summary,
                tags       = excluded.tags,
                category   = excluded.category,
                source     = excluded.source,
                scope      = excluded.scope,
                visibility = 'shared',
                session_id = excluded.session_id,
                created_at = excluded.created_at,
                is_deleted = excluded.is_deleted,
                deleted_at = excluded.deleted_at,
                pinned     = excluded.pinned,
                links      = excluded.links,
                content_hash = excluded.content_hash
            WHERE memories.content    IS NOT excluded.content
               OR memories.summary    IS NOT excluded.summary
               OR memories.tags       IS NOT excluded.tags
               OR memories.category   IS NOT excluded.category
               OR memories.source     IS NOT excluded.source
               OR memories.scope      IS NOT excluded.scope
               OR memories.visibility IS NOT 'shared'
               OR memories.session_id IS NOT excluded.session_id
               OR memories.created_at IS NOT excluded.created_at
               OR memories.is_deleted IS NOT excluded.is_deleted
               OR memories.deleted_at IS NOT excluded.deleted_at
               OR memories.pinned     IS NOT excluded.pinned
               OR memories.links      IS NOT excluded.links
            """;
        cmd.Parameters.AddWithValue("$id", m.Id);
        cmd.Parameters.AddWithValue("$content", m.Content);
        cmd.Parameters.AddWithValue("$summary", m.Summary);
        cmd.Parameters.AddWithValue("$tags", string.Join(',', m.Tags));
        cmd.Parameters.AddWithValue("$category", m.Category);
        cmd.Parameters.AddWithValue("$source", m.Source);
        cmd.Parameters.AddWithValue("$scope", m.Scope);
        cmd.Parameters.AddWithValue("$session", (object?)m.SessionId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$created", m.CreatedAt);
        cmd.Parameters.AddWithValue("$isDeleted", m.DeletedAt is null ? 0 : 1);
        cmd.Parameters.AddWithValue("$deletedAt", (object?)m.DeletedAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$pinned", m.Pinned ? 1 : 0);
        cmd.Parameters.AddWithValue("$links", (object?)SerializeLinks([.. m.Links]) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$hash", ComputeContentHash(m.Content));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task InsertRelationIfPresentAsync(MemoryRelation r, CancellationToken ct)
    {
        await using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO memory_relations (from_id, to_id, relationship, created_at)
            SELECT $from, $to, $rel, $created
            WHERE  EXISTS (SELECT 1 FROM memories WHERE id = $from)
              AND  EXISTS (SELECT 1 FROM memories WHERE id = $to)
            """;
        cmd.Parameters.AddWithValue("$from", r.FromId);
        cmd.Parameters.AddWithValue("$to", r.ToId);
        cmd.Parameters.AddWithValue("$rel", r.Relationship);
        cmd.Parameters.AddWithValue("$created", r.CreatedAt);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task SetVisibilityAsync(string id, string visibility, CancellationToken ct)
    {
        await using var cmd = _db.CreateCommand();
        cmd.CommandText = "UPDATE memories SET visibility = $visibility WHERE id = $id";
        cmd.Parameters.AddWithValue("$visibility", visibility);
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static bool HasLinkPath(string? linksJson, string needle)
    {
        if (string.IsNullOrWhiteSpace(linksJson)) return false;
        try
        {
            var links = DeserializeLinks(linksJson);
            return links.Any(l =>
                l.Path.Contains(needle, StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }

    private async Task<MemoryEntry?> FindLiveByHashAsync(string hash, CancellationToken ct)
    {
        await using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            SELECT id, content, summary, tags, category, source,
                   scope, visibility, session_id,
                   created_at, accessed_at, access_count, is_deleted, deleted_at,
                   pinned, links
            FROM   memories
            WHERE  content_hash = $hash AND is_deleted = 0
            ORDER  BY created_at DESC
            LIMIT  1
            """;
        cmd.Parameters.AddWithValue("$hash", hash);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadEntry(reader) : null;
    }

    private async Task BumpAccessAsync(IReadOnlyList<string> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var inList = string.Join(',', ids.Select(i => $"'{i.Replace("'", "''")}'"));
        await using var upd = _db.CreateCommand();
        upd.CommandText = $"""
            UPDATE memories
            SET accessed_at  = {now},
                access_count = access_count + 1
            WHERE id IN ({inList})
            """;
        await upd.ExecuteNonQueryAsync(ct);
    }

    private static string ComputeContentHash(string content)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(content), hash);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string? SerializeLinks(MemoryLink[] links)
        => links is null || links.Length == 0
            ? null
            : JsonSerializer.Serialize(links, LinkJsonOptions);

    public static MemoryLink[] DeserializeLinks(string? json)
        => string.IsNullOrWhiteSpace(json)
            ? []
            : JsonSerializer.Deserialize<MemoryLink[]>(json, LinkJsonOptions) ?? [];

    private static MemoryEntry ReadEntry(SqliteDataReader r) => new(
        Id: r.GetString(0),
        Content: r.GetString(1),
        Summary: r.GetString(2),
        Tags: r.GetString(3),
        Category: r.GetString(4),
        Source: r.GetString(5),
        Scope: r.GetString(6),
        Visibility: r.GetString(7),
        SessionId: r.IsDBNull(8) ? null : r.GetString(8),
        CreatedAt: r.GetInt64(9),
        AccessedAt: r.GetInt64(10),
        AccessCount: r.GetInt32(11),
        IsDeleted: r.GetBoolean(12),
        DeletedAt: r.IsDBNull(13) ? null : r.GetInt64(13),
        Pinned: !r.IsDBNull(14) && r.GetInt64(14) != 0,
        LinksJson: r.IsDBNull(15) ? null : r.GetString(15));

    private static async Task ApplySchemaAsync(SqliteConnection db, CancellationToken ct)
    {
        // Base schema (idempotent).
        await using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = """
                PRAGMA journal_mode = WAL;
                PRAGMA foreign_keys = ON;

                CREATE TABLE IF NOT EXISTS memories (
                    id           TEXT    PRIMARY KEY,
                    content      TEXT    NOT NULL,
                    summary      TEXT    NOT NULL,
                    tags         TEXT    NOT NULL DEFAULT '',
                    category     TEXT    NOT NULL
                                 CHECK(category IN ('fact','preference','pattern','decision','history','note')),
                    source       TEXT    NOT NULL DEFAULT 'ai'
                                 CHECK(source IN ('ai','user')),
                    scope        TEXT    NOT NULL DEFAULT 'project'
                                 CHECK(scope IN ('project','global')),
                    visibility   TEXT    NOT NULL DEFAULT 'private'
                                 CHECK(visibility IN ('shared','private')),
                    session_id   TEXT,
                    created_at   INTEGER NOT NULL,
                    accessed_at  INTEGER NOT NULL,
                    access_count INTEGER NOT NULL DEFAULT 0,
                    is_deleted   INTEGER NOT NULL DEFAULT 0,
                    deleted_at   INTEGER,
                    embedding    BLOB
                );

                CREATE VIRTUAL TABLE IF NOT EXISTS memories_fts USING fts5(
                    content,
                    summary,
                    tags,
                    content='memories',
                    content_rowid='rowid',
                    tokenize='porter unicode61'
                );

                CREATE TRIGGER IF NOT EXISTS memories_ai AFTER INSERT ON memories BEGIN
                    INSERT INTO memories_fts(rowid, content, summary, tags)
                    VALUES (new.rowid, new.content, new.summary, new.tags);
                END;

                CREATE TRIGGER IF NOT EXISTS memories_ad AFTER DELETE ON memories BEGIN
                    INSERT INTO memories_fts(memories_fts, rowid, content, summary, tags)
                    VALUES ('delete', old.rowid, old.content, old.summary, old.tags);
                END;

                CREATE TRIGGER IF NOT EXISTS memories_au AFTER UPDATE ON memories BEGIN
                    INSERT INTO memories_fts(memories_fts, rowid, content, summary, tags)
                    VALUES ('delete', old.rowid, old.content, old.summary, old.tags);
                    INSERT INTO memories_fts(rowid, content, summary, tags)
                    VALUES (new.rowid, new.content, new.summary, new.tags);
                END;

                CREATE TABLE IF NOT EXISTS memory_relations (
                    from_id      TEXT    NOT NULL REFERENCES memories(id),
                    to_id        TEXT    NOT NULL REFERENCES memories(id),
                    relationship TEXT    NOT NULL
                                 CHECK(relationship IN ('supersedes','supports','contradicts','related_to')),
                    created_at   INTEGER NOT NULL,
                    PRIMARY KEY (from_id, to_id, relationship)
                );

                -- Ids .tosh/memories/ held a file for at the last sync, which is how an import tells
                -- a memory whose file went away from one that never had a file.
                CREATE TABLE IF NOT EXISTS shared_file_ids (
                    id TEXT PRIMARY KEY
                );

                CREATE INDEX IF NOT EXISTS ix_memories_category ON memories(category) WHERE is_deleted = 0;
                CREATE INDEX IF NOT EXISTS ix_memories_scope    ON memories(scope)    WHERE is_deleted = 0;
                CREATE INDEX IF NOT EXISTS ix_memories_session  ON memories(session_id) WHERE is_deleted = 0;
                CREATE INDEX IF NOT EXISTS ix_memories_accessed ON memories(accessed_at DESC) WHERE is_deleted = 0;
                """;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // Idempotent column migrations. SQLite has no ADD COLUMN IF NOT EXISTS,
        // so we probe table_info() and add what's missing.
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var probe = db.CreateCommand())
        {
            probe.CommandText = "PRAGMA table_info(memories)";
            await using var pr = await probe.ExecuteReaderAsync(ct);
            while (await pr.ReadAsync(ct))
                existing.Add(pr.GetString(1));
        }

        async Task AddColumnIfMissing(string name, string ddl)
        {
            if (existing.Contains(name)) return;
            await using var cmd = db.CreateCommand();
            cmd.CommandText = $"ALTER TABLE memories ADD COLUMN {ddl}";
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await AddColumnIfMissing("pinned", "pinned INTEGER NOT NULL DEFAULT 0");
        await AddColumnIfMissing("links", "links TEXT");
        await AddColumnIfMissing("content_hash", "content_hash TEXT");

        // Backfill content_hash for rows inserted before the column existed.
        // Only touches rows that need it.
        await using (var probe = db.CreateCommand())
        {
            probe.CommandText = "SELECT id, content FROM memories WHERE content_hash IS NULL";
            await using var pr = await probe.ExecuteReaderAsync(ct);
            var pending = new List<(string Id, string Hash)>();
            while (await pr.ReadAsync(ct))
                pending.Add((pr.GetString(0), ComputeContentHash(pr.GetString(1))));
            foreach (var (id, hash) in pending)
            {
                await using var upd = db.CreateCommand();
                upd.CommandText = "UPDATE memories SET content_hash = $h WHERE id = $id";
                upd.Parameters.AddWithValue("$h", hash);
                upd.Parameters.AddWithValue("$id", id);
                await upd.ExecuteNonQueryAsync(ct);
            }
        }

        await using (var idx = db.CreateCommand())
        {
            idx.CommandText = """
                CREATE INDEX IF NOT EXISTS ix_memories_pinned ON memories(pinned) WHERE is_deleted = 0 AND pinned = 1;
                CREATE INDEX IF NOT EXISTS ix_memories_hash   ON memories(content_hash) WHERE is_deleted = 0;
                """;
            await idx.ExecuteNonQueryAsync(ct);
        }
    }
}
