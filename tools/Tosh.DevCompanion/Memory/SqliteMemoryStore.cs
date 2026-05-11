using Microsoft.Data.Sqlite;

namespace Tosh.DevCompanion.Memory;

public sealed class SqliteMemoryStore : IMemoryStore
{
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

    public async Task<MemoryEntry> StoreAsync(StoreRequest req, CancellationToken ct = default)
    {
        var id  = Guid.NewGuid().ToString();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var tags = string.Join(',', req.Tags);

        await using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO memories
                (id, content, summary, tags, category, source, scope, visibility,
                 session_id, created_at, accessed_at, access_count, is_deleted)
            VALUES
                ($id, $content, $summary, $tags, $category, $source, $scope, $visibility,
                 $session, $now, $now, 0, 0)
            """;
        cmd.Parameters.AddWithValue("$id",         id);
        cmd.Parameters.AddWithValue("$content",    req.Content);
        cmd.Parameters.AddWithValue("$summary",    req.Summary);
        cmd.Parameters.AddWithValue("$tags",       tags);
        cmd.Parameters.AddWithValue("$category",   req.Category);
        cmd.Parameters.AddWithValue("$source",     req.Source);
        cmd.Parameters.AddWithValue("$scope",      req.Scope);
        cmd.Parameters.AddWithValue("$visibility", req.Visibility);
        cmd.Parameters.AddWithValue("$session",    req.SessionId ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$now",        now);
        await cmd.ExecuteNonQueryAsync(ct);

        return (await GetAsync(id, ct))!;
    }

    public async Task<RecallResult> RecallAsync(RecallRequest req, CancellationToken ct = default)
    {
        var limit = Math.Clamp(req.Limit, 1, 50);

        await using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            SELECT m.id, m.content, m.summary, m.tags, m.category, m.source,
                   m.scope, m.visibility, m.session_id,
                   m.created_at, m.accessed_at, m.access_count,
                   m.is_deleted, m.deleted_at,
                   rank AS relevance
            FROM   memories_fts f
            JOIN   memories m ON m.rowid = f.rowid
            WHERE  memories_fts MATCH $query
              AND  m.is_deleted = 0
              AND  ($category IS NULL OR m.category = $category)
              AND  ($scope = 'all' OR m.scope = $scope)
            ORDER  BY rank, m.accessed_at DESC
            LIMIT  $limit
            """;
        cmd.Parameters.AddWithValue("$query",    req.Query);
        cmd.Parameters.AddWithValue("$category", req.Category ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$scope",    req.Scope);
        cmd.Parameters.AddWithValue("$limit",    limit);

        var results = new List<ScoredMemory>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var entry = ReadEntry(reader);
            var score = reader.IsDBNull(14) ? 0.0 : -reader.GetDouble(14); // FTS5 rank is negative
            results.Add(new ScoredMemory(entry, Math.Round(score, 4)));
        }

        // Update access metadata for matched rows
        if (results.Count > 0)
        {
            var ids  = string.Join(',', results.Select(r => $"'{r.Entry.Id}'"));
            var now  = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            await using var upd = _db.CreateCommand();
            upd.CommandText = $"""
                UPDATE memories
                SET accessed_at  = {now},
                    access_count = access_count + 1
                WHERE id IN ({ids})
                """;
            await upd.ExecuteNonQueryAsync(ct);
        }

        // Tag post-filter (SQLite FTS5 doesn't support per-column tag array queries)
        if (req.Tags.Length > 0)
        {
            results = results
                .Where(r => req.Tags.All(t => r.Entry.TagList.Contains(t, StringComparer.OrdinalIgnoreCase)))
                .ToList();
        }

        return new RecallResult(results, results.Count, req.Query);
    }

    public async Task<IReadOnlyList<MemoryEntry>> ListAsync(ListRequest req, CancellationToken ct = default)
    {
        var limit = Math.Clamp(req.Limit, 1, 200);

        await using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            SELECT id, content, summary, tags, category, source,
                   scope, visibility, session_id,
                   created_at, accessed_at, access_count, is_deleted, deleted_at
            FROM   memories
            WHERE  is_deleted = 0
              AND  ($category IS NULL OR category = $category)
              AND  ($scope = 'all' OR scope = $scope)
              AND  ($sinceSession IS NULL OR session_id >= $sinceSession)
            ORDER  BY created_at DESC
            LIMIT  $limit
            """;
        cmd.Parameters.AddWithValue("$category",     req.Category ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$scope",        req.Scope);
        cmd.Parameters.AddWithValue("$sinceSession", req.SinceSession ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$limit",        limit);

        var entries = new List<MemoryEntry>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            entries.Add(ReadEntry(reader));

        // Tag post-filter
        if (req.Tags.Length > 0)
        {
            entries = entries
                .Where(e => req.Tags.All(t => e.TagList.Contains(t, StringComparer.OrdinalIgnoreCase)))
                .ToList();
        }

        // Strip content when caller only wants summaries
        if (!req.IncludeContent)
            entries = entries.Select(e => e with { Content = string.Empty }).ToList();

        return entries;
    }

    public async Task<MemoryEntry?> GetAsync(string id, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            SELECT id, content, summary, tags, category, source,
                   scope, visibility, session_id,
                   created_at, accessed_at, access_count, is_deleted, deleted_at
            FROM   memories
            WHERE  id = $id
            """;
        cmd.Parameters.AddWithValue("$id", id);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadEntry(reader) : null;
    }

    public async Task<bool> ForgetAsync(ForgetRequest req, CancellationToken ct = default)
    {
        var entry = await GetAsync(req.Id, ct);
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
        cmd.Parameters.AddWithValue("$id",  req.Id);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task RelateAsync(string fromId, string toId, string relationship, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO memory_relations (from_id, to_id, relationship, created_at)
            VALUES ($from, $to, $rel, $now)
            """;
        cmd.Parameters.AddWithValue("$from", fromId);
        cmd.Parameters.AddWithValue("$to",   toId);
        cmd.Parameters.AddWithValue("$rel",  relationship);
        cmd.Parameters.AddWithValue("$now",  now);
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

    public void Dispose() => _db.Dispose();

    // ── Private helpers ───────────────────────────────────────────────────────

    private static MemoryEntry ReadEntry(SqliteDataReader r) => new(
        Id:          r.GetString(0),
        Content:     r.GetString(1),
        Summary:     r.GetString(2),
        Tags:        r.GetString(3),
        Category:    r.GetString(4),
        Source:      r.GetString(5),
        Scope:       r.GetString(6),
        Visibility:  r.GetString(7),
        SessionId:   r.IsDBNull(8)  ? null : r.GetString(8),
        CreatedAt:   r.GetInt64(9),
        AccessedAt:  r.GetInt64(10),
        AccessCount: r.GetInt32(11),
        IsDeleted:   r.GetBoolean(12),
        DeletedAt:   r.IsDBNull(13) ? null : r.GetInt64(13));

    private static async Task ApplySchemaAsync(SqliteConnection db, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand();
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
}
