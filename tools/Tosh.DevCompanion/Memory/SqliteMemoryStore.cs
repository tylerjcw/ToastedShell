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

    private SqliteMemoryStore(SqliteConnection db) => _db = db;

    public static async Task<SqliteMemoryStore> OpenAsync(string dbPath, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        var conn = new SqliteConnection($"Data Source={dbPath}");
        await conn.OpenAsync(ct);
        await ApplySchemaAsync(conn, ct);
        return new SqliteMemoryStore(conn);
    }

    // ── IMemoryStore ──────────────────────────────────────────────────────────

    public async Task<StoreResult> StoreAsync(StoreRequest req, CancellationToken ct = default)
    {
        // Dedup-on-store via SHA-256 of the content body. Same body, not
        // tombstoned → bump accessed_at, return existing row with Deduped=true.
        var hash = ComputeContentHash(req.Content);
        var existing = await FindLiveByHashAsync(hash, ct);
        if (existing is not null)
        {
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
        var results = new List<StoreResult>(requests.Count);
        await using var tx = (SqliteTransaction)await _db.BeginTransactionAsync(ct);
        foreach (var req in requests)
            results.Add(await StoreAsync(req, ct));
        await tx.CommitAsync(ct);
        return results;
    }

    public async Task<MemoryEntry> UpdateAsync(UpdateRequest req, CancellationToken ct = default)
    {
        var resolved = await ResolveIdAsync(req.Id, ct)
            ?? throw new InvalidOperationException($"No memory matches id '{req.Id}'.");
        var current = await GetAsync(resolved, ct)
            ?? throw new InvalidOperationException($"Memory '{resolved}' not found.");

        // Content edits are non-destructive: insert a new row, link old→new
        // via supersedes, then tombstone the old. Preserves audit history.
        if (req.Content is not null && req.Content != current.Content)
        {
            var migrated = await StoreAsync(new StoreRequest(
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

            await RelateAsync(migrated.Entry.Id, current.Id, "supersedes", ct);
            await ForgetAsync(new ForgetRequest(current.Id, Confirm: true,
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

        // Need at least 4 chars to attempt a prefix match — avoids accidental
        // catastrophic resolutions when someone passes a single character.
        if (idOrPrefix.Length < 4) return null;

        await using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT id FROM memories WHERE id LIKE $prefix LIMIT 2";
        cmd.Parameters.AddWithValue("$prefix", idOrPrefix + "%");
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        if (!await reader.ReadAsync(ct)) return null;
        var first = reader.GetString(0);
        if (await reader.ReadAsync(ct))
            throw new InvalidOperationException($"Ambiguous id prefix '{idOrPrefix}' — at least two matches.");
        return first;
    }

    public async Task<bool> ForgetAsync(ForgetRequest req, CancellationToken ct = default)
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
