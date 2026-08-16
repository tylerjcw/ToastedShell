using System.Text.Json;
using System.Text.Json.Nodes;
using Tosh.DevCompanion.Memory;

namespace Tosh.DevCompanion;

/// <summary>
/// A minimal JSON-RPC / MCP server that exposes the memory tools.
/// Reads from stdin, writes to stdout — same transport as Tosh.Mcp.
/// </summary>
public sealed class McpMemoryServer(IMemoryStore store)
{
    private static readonly JsonSerializerOptions EnvelopeOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    private static readonly JsonSerializerOptions PayloadOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public async Task RunAsync(CancellationToken ct = default)
    {
        using var stdin = Console.OpenStandardInput();
        using var stdout = Console.OpenStandardOutput();
        var buffer = new byte[1024 * 64];

        while (!ct.IsCancellationRequested)
        {
            var line = await ReadLineAsync(stdin, buffer, ct);
            if (line is null) break;
            if (string.IsNullOrWhiteSpace(line)) continue;

            try
            {
                using var doc = JsonDocument.Parse(line);
                await DispatchAsync(doc.RootElement, stdout, ct);
            }
            catch { /* malformed request — ignore */ }
        }
    }

    // ── Dispatch ──────────────────────────────────────────────────────────────

    private async Task DispatchAsync(JsonElement msg, Stream stdout, CancellationToken ct)
    {
        var hasId = msg.TryGetProperty("id", out var idEl) && idEl.ValueKind != JsonValueKind.Undefined && idEl.ValueKind != JsonValueKind.Null;
        var method = msg.TryGetProperty("method", out var methodEl) ? methodEl.GetString() : null;
        var @params = msg.TryGetProperty("params", out var paramsEl) ? paramsEl : default;

        // Ignore JSON-RPC notifications (messages without an ID)
        if (!hasId || string.IsNullOrEmpty(method) || method.StartsWith("notifications/"))
        {
            return;
        }

        try
        {
            object result = method switch
            {
                "initialize" => await InitializeAsync(ct),
                "ping" => new { },
                "tools/list" => new { tools = ToolDefinitions },
                "tools/call" => await HandleToolCallAsync(@params, ct),
                "resources/list" => new { resources = Array.Empty<object>() },
                "resources/templates/list" => new { resourceTemplates = Array.Empty<object>() },
                "prompts/list" => new { prompts = Array.Empty<object>() },
                _ => throw new InvalidOperationException($"Method '{method}' is not supported.")
            };

            await WriteResponseAsync(idEl, result, stdout, ct);
        }
        catch (Exception ex)
        {
            await WriteErrorResponseAsync(idEl, -32601, ex.Message, stdout, ct);
        }
    }

    private async Task<object> InitializeAsync(CancellationToken ct)
    {
        // Surface up to 6 pinned project memories as part of serverInfo.instructions
        // so capable hosts can prime the agent without an extra recall round-trip.
        // Hosts that ignore the field cost nothing; capable ones save ~one tool call.
        var pinned = await store.GetPinnedAsync("project", 6, ct);
        var instructions = pinned.Count == 0
            ? "Tōsh Dev Companion: persistent project memory. Call memory_recall before exploring."
            : "Pinned project facts:\n" +
              string.Join('\n', pinned.Select(p => $"- [{p.ShortId}] {p.Summary}"));

        return new
        {
            protocolVersion = "2024-11-05",
            capabilities = new { tools = new { } },
            serverInfo = new { name = "tosh-dev-companion", version = "1.2" },
            instructions
        };
    }

    private async Task<object> HandleToolCallAsync(JsonElement @params, CancellationToken ct)
    {
        var name = @params.GetProperty("name").GetString() ?? string.Empty;
        var args = @params.TryGetProperty("arguments", out var a) ? a : default;

        return name switch
        {
            "memory_store" => await MemoryStoreAsync(args, ct),
            "memory_store_batch" => await MemoryStoreBatchAsync(args, ct),
            "memory_update" => await MemoryUpdateAsync(args, ct),
            "memory_recall" => await MemoryRecallAsync(args, ct),
            "memory_list" => await MemoryListAsync(args, ct),
            "memory_forget" => await MemoryForgetAsync(args, ct),
            "memory_relate" => await MemoryRelateAsync(args, ct),
            "memory_tags" => await MemoryTagsAsync(ct),
            "memory_graph" => await MemoryGraphAsync(args, ct),
            "memory_open" => await MemoryOpenAsync(args, ct),
            _ => ErrorContent($"Unknown tool '{name}'.")
        };
    }

    // ── Tool handlers ─────────────────────────────────────────────────────────

    private async Task<object> MemoryStoreAsync(JsonElement args, CancellationToken ct)
    {
        var req = ParseStoreRequest(args);
        var result = await store.StoreAsync(req, ct);

        // Minimal echo. Caller already knows what it sent; only the id is new.
        return OkContent(new
        {
            id = result.Entry.Id,
            short_id = result.Entry.ShortId,
            deduped = result.Deduped ? true : (bool?)null
        });
    }

    private async Task<object> MemoryStoreBatchAsync(JsonElement args, CancellationToken ct)
    {
        var entries = args.GetProperty("entries");
        var reqs = new List<StoreRequest>(entries.GetArrayLength());
        foreach (var el in entries.EnumerateArray())
            reqs.Add(ParseStoreRequest(el));

        var results = await store.StoreBatchAsync(reqs, ct);

        // Apply per-entry relate hints. References of the form "#N" point at
        // sibling result N; everything else is treated as an existing id/prefix.
        var idx = 0;
        foreach (var el in entries.EnumerateArray())
        {
            if (el.TryGetProperty("relate", out var rel) && rel.ValueKind == JsonValueKind.Object)
            {
                var toRaw = rel.GetProperty("to_id").GetString()!;
                var relationship = rel.GetProperty("relationship").GetString()!;
                var toId = toRaw.StartsWith('#') && int.TryParse(toRaw[1..], out var n) && n >= 0 && n < results.Count
                    ? results[n].Entry.Id
                    : toRaw;
                await store.RelateAsync(results[idx].Entry.Id, toId, relationship, ct);
            }
            idx++;
        }

        return OkContent(new
        {
            ids = results.Select(r => r.Entry.Id).ToArray(),
            short_ids = results.Select(r => r.Entry.ShortId).ToArray(),
            deduped = results.Select((r, i) => r.Deduped ? i : -1).Where(i => i >= 0).ToArray()
        });
    }

    private async Task<object> MemoryUpdateAsync(JsonElement args, CancellationToken ct)
    {
        var links = args.TryGetProperty("links", out var l) && l.ValueKind == JsonValueKind.Array
            ? ParseLinks(l)
            : null;

        var req = new UpdateRequest(
            Id: args.GetProperty("id").GetString()!,
            Summary: args.OptStringOrNull("summary"),
            Content: args.OptStringOrNull("content"),
            Tags: args.TryGetProperty("tags", out _) ? args.OptStringArray("tags") : null,
            Scope: args.OptStringOrNull("scope"),
            Visibility: args.OptStringOrNull("visibility"),
            Pinned: args.TryGetProperty("pinned", out var p) ? p.GetBoolean() : null,
            Links: links);

        var entry = await store.UpdateAsync(req, ct);
        return OkContent(new { id = entry.Id, short_id = entry.ShortId });
    }

    private async Task<object> MemoryRecallAsync(JsonElement args, CancellationToken ct)
    {
        var mode = args.OptString("mode", "snippet");
        var verbose = args.OptBool("verbose", false);

        var req = new RecallRequest(
            Query: args.GetProperty("query").GetString()!,
            Limit: args.OptInt("limit", 10),
            Category: args.OptStringOrNull("category"),
            Scope: args.OptString("scope", "all"),
            Tags: args.OptStringArray("tags"),
            Mode: mode,
            Verbose: verbose,
            LinksPath: args.OptStringOrNull("links_path"));

        var result = await store.RecallAsync(req, ct);
        var scopeFilter = req.Scope;

        return OkContent(new
        {
            results = result.Results.Select(r => SerializeHit(r, mode, verbose, scopeFilter)).ToArray(),
            total = result.Total
        });
    }

    private async Task<object> MemoryListAsync(JsonElement args, CancellationToken ct)
    {
        var req = new ListRequest(
            Category: args.OptStringOrNull("category"),
            Scope: args.OptString("scope", "all"),
            Tags: args.OptStringArray("tags"),
            SinceSession: args.OptStringOrNull("since_session"),
            IncludeContent: args.OptBool("include_content", false),
            Limit: args.OptInt("limit", 50),
            MinAgeDays: args.TryGetProperty("min_age_days", out var m) && m.TryGetInt32(out var mi) ? mi : null,
            MaxAccessCount: args.TryGetProperty("max_access_count", out var mx) && mx.TryGetInt32(out var mxi) ? mxi : null);

        var verbose = args.OptBool("verbose", false);
        var entries = await store.ListAsync(req, ct);

        return OkContent(new
        {
            memories = entries.Select(e => SerializeEntry(e, req.IncludeContent, verbose, req.Scope)).ToArray(),
            total = entries.Count
        });
    }

    private async Task<object> MemoryForgetAsync(JsonElement args, CancellationToken ct)
    {
        var req = new ForgetRequest(
            Id: args.GetProperty("id").GetString()!,
            Confirm: args.OptBool("confirm", false),
            Reason: args.OptStringOrNull("reason"));

        try
        {
            var deleted = await store.ForgetAsync(req, ct);
            return OkContent(new { deleted, id = req.Id });
        }
        catch (InvalidOperationException ex)
        {
            return ErrorContent(ex.Message);
        }
    }

    private async Task<object> MemoryRelateAsync(JsonElement args, CancellationToken ct)
    {
        var fromId = args.GetProperty("from_id").GetString()!;
        var relationship = args.GetProperty("relationship").GetString()!;
        var toProp = args.GetProperty("to_id");

        var targets = toProp.ValueKind == JsonValueKind.Array
            ? toProp.EnumerateArray().Select(e => e.GetString()!).ToArray()
            : [toProp.GetString()!];

        foreach (var t in targets)
            await store.RelateAsync(fromId, t, relationship, ct);

        return OkContent(new { related = targets.Length });
    }

    private async Task<object> MemoryTagsAsync(CancellationToken ct)
    {
        var tags = await store.GetTagsAsync(ct);
        return OkContent(new { tags = tags.Select(t => new { t.Tag, t.Count }).ToArray() });
    }

    private async Task<object> MemoryGraphAsync(JsonElement args, CancellationToken ct)
    {
        var seeds = args.TryGetProperty("seed", out var s) && s.ValueKind == JsonValueKind.Array
            ? s.EnumerateArray().Select(e => e.GetString()!).ToArray()
            : new[] { args.GetProperty("seed").GetString()! };

        var graph = await store.GetGraphAsync(new GraphRequest(
            Seeds: seeds,
            Depth: args.OptInt("depth", 1),
            Relationship: args.OptStringOrNull("relationship"),
            IncludeContent: args.OptBool("include_content", false)), ct);

        return OkContent(new
        {
            nodes = graph.Nodes.Select(e => new
            {
                id = e.Id,
                short_id = e.ShortId,
                summary = e.Summary,
                category = e.Category,
                content = string.IsNullOrEmpty(e.Content) ? null : e.Content
            }).ToArray(),
            edges = graph.Edges.Select(r => new
            {
                from = r.FromId,
                from_short = r.FromId.Length >= 8 ? r.FromId[..8] : r.FromId,
                to = r.ToId,
                to_short = r.ToId.Length >= 8 ? r.ToId[..8] : r.ToId,
                rel = r.Relationship
            }).ToArray()
        });
    }

    private async Task<object> MemoryOpenAsync(JsonElement args, CancellationToken ct)
    {
        var idArg = args.GetProperty("id").GetString()!;
        var resolved = await store.ResolveIdAsync(idArg, ct)
            ?? throw new InvalidOperationException($"No memory matches id '{idArg}'.");
        var entry = await store.GetAsync(resolved, ct)
            ?? throw new InvalidOperationException($"Memory '{resolved}' not found.");

        var cwd = args.OptStringOrNull("cwd") ?? Directory.GetCurrentDirectory();
        var links = SqliteMemoryStore.DeserializeLinks(entry.LinksJson);

        return OkContent(new
        {
            id = entry.Id,
            short_id = entry.ShortId,
            summary = entry.Summary,
            links = links.Select(l =>
            {
                var abs = Path.IsPathRooted(l.Path) ? l.Path : Path.GetFullPath(Path.Combine(cwd, l.Path));
                var fragment = l.Line is int ln
                    ? (l.LineEnd is int le && le > ln ? $"#L{ln}-L{le}" : $"#L{ln}")
                    : string.Empty;
                return new
                {
                    path = l.Path,
                    abs_path = abs,
                    uri = $"file://{Uri.EscapeDataString(abs).Replace("%2F", "/").Replace("%3A", ":")}{fragment}",
                    line = l.Line,
                    line_end = l.LineEnd,
                    kind = l.Kind
                };
            }).ToArray()
        });
    }

    // ── Serialization helpers ─────────────────────────────────────────────────

    private static object SerializeHit(ScoredMemory r, string mode, bool verbose, string scopeFilter)
    {
        var e = r.Entry;
        return BuildEntryObject(e, includeContent: mode == "full", verbose, scopeFilter,
            extras: new Dictionary<string, object?>
            {
                ["score"] = Math.Round(r.RelevanceScore, 3),
                ["snippet"] = r.Snippet
            });
    }

    private static object SerializeEntry(MemoryEntry e, bool includeContent, bool verbose, string scopeFilter)
        => BuildEntryObject(e, includeContent, verbose, scopeFilter, extras: null);

    private static object BuildEntryObject(
        MemoryEntry e,
        bool includeContent,
        bool verbose,
        string scopeFilter,
        IDictionary<string, object?>? extras)
    {
        var obj = new Dictionary<string, object?>
        {
            ["id"] = verbose ? e.Id : null,
            ["short_id"] = e.ShortId,
            ["summary"] = e.Summary,
            ["category"] = e.Category
        };

        if (!verbose) obj["id"] = e.Id; // keep id always; suppression was overzealous

        if (includeContent && !string.IsNullOrEmpty(e.Content)) obj["content"] = e.Content;
        if (e.TagList.Length > 0) obj["tags"] = e.TagList;
        if (scopeFilter == "all") obj["scope"] = e.Scope;
        if (e.Pinned) obj["pinned"] = true;
        if (!string.IsNullOrEmpty(e.LinksJson))
            obj["links"] = JsonNode.Parse(e.LinksJson);

        if (verbose)
        {
            obj["created_at"] = e.CreatedAtUtc.ToString("O");
            obj["access_count"] = e.AccessCount;
            obj["visibility"] = e.Visibility;
        }
        else
        {
            obj["created"] = e.CreatedAtUtc.ToString("yyyy-MM-dd");
        }

        if (extras is not null)
            foreach (var (k, v) in extras)
                if (v is not null) obj[k] = v;

        return obj;
    }

    private static StoreRequest ParseStoreRequest(JsonElement el)
    {
        var links = el.TryGetProperty("links", out var l) && l.ValueKind == JsonValueKind.Array
            ? ParseLinks(l)
            : null;

        return new StoreRequest(
            Content: el.GetProperty("content").GetString()!,
            Summary: el.GetProperty("summary").GetString()!,
            Category: el.GetProperty("category").GetString()!,
            Source: el.OptString("source", "ai"),
            Scope: el.OptString("scope", "project"),
            Visibility: el.OptString("visibility", "private"),
            Tags: el.OptStringArray("tags"),
            SessionId: el.OptStringOrNull("session_id"),
            Pinned: el.OptBool("pinned", false),
            Links: links ?? []);
    }

    private static MemoryLink[] ParseLinks(JsonElement arr)
    {
        var list = new List<MemoryLink>(arr.GetArrayLength());
        foreach (var el in arr.EnumerateArray())
        {
            list.Add(new MemoryLink(
                Path: el.GetProperty("path").GetString()!,
                Line: el.TryGetProperty("line", out var ln) && ln.TryGetInt32(out var lni) ? lni : null,
                LineEnd: el.TryGetProperty("line_end", out var le) && le.TryGetInt32(out var lei) ? lei : null,
                Kind: el.OptStringOrNull("kind")));
        }
        return [.. list];
    }

    // ── Transport ─────────────────────────────────────────────────────────────

    private async Task WriteResponseAsync(JsonElement id, object? result, Stream stdout, CancellationToken ct)
    {
        var response = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = JsonNode.Parse(id.ValueKind == JsonValueKind.Undefined ? "null" : id.GetRawText())
        };

        if (result is not null)
            response["result"] = JsonSerializer.SerializeToNode(result, EnvelopeOptions);

        var bytes = System.Text.Encoding.UTF8.GetBytes(response.ToJsonString() + "\n");
        await _writeLock.WaitAsync(ct);
        try { await stdout.WriteAsync(bytes, ct); await stdout.FlushAsync(ct); }
        finally { _writeLock.Release(); }
    }

    private async Task WriteErrorResponseAsync(JsonElement id, int code, string message, Stream stdout, CancellationToken ct)
    {
        if (id.ValueKind == JsonValueKind.Undefined) return;

        var response = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = JsonNode.Parse(id.GetRawText()),
            ["error"] = new JsonObject
            {
                ["code"] = code,
                ["message"] = message
            }
        };

        var bytes = System.Text.Encoding.UTF8.GetBytes(response.ToJsonString() + "\n");
        await _writeLock.WaitAsync(ct);
        try { await stdout.WriteAsync(bytes, ct); await stdout.FlushAsync(ct); }
        finally { _writeLock.Release(); }
    }

    private static async Task<string?> ReadLineAsync(Stream stream, byte[] buf, CancellationToken ct)
    {
        var sb = new System.Text.StringBuilder();
        var single = new byte[1];
        while (true)
        {
            var read = await stream.ReadAsync(single, ct);
            if (read == 0) return sb.Length > 0 ? sb.ToString() : null;
            var ch = (char)single[0];
            if (ch == '\n') return sb.ToString();
            sb.Append(ch);
        }
    }

    private static object OkContent(object data) =>
        new { content = new[] { new { type = "text", text = JsonSerializer.Serialize(data, PayloadOptions) } } };

    private static object ErrorContent(string message) =>
        new { content = new[] { new { type = "text", text = $"Error: {message}" } }, isError = true };

    // ── Tool definitions ──────────────────────────────────────────────────────

    private static readonly object LinkSchema = new
    {
        type = "object",
        properties = new
        {
            path = new { type = "string", description = "Workspace-relative or absolute file path." },
            line = new { type = "integer", description = "1-based start line." },
            line_end = new { type = "integer", description = "1-based inclusive end line." },
            kind = new { type = "string", description = "Optional tag (e.g. 'def', 'ref', 'doc')." }
        },
        required = new[] { "path" }
    };

    private static readonly object[] ToolDefinitions =
    [
        new
        {
            name = "memory_store",
            description = """
                Store a single memory. Returns {id, short_id}; if an identical
                content body already exists, returns the existing id with
                deduped=true instead of inserting a duplicate.
                Use memory_store_batch for multiple inserts in one call.
                """,
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    content    = new { type = "string",  description = "Full memory body. FTS index uses this." },
                    summary    = new { type = "string",  description = "≤120 char line surfaced under token pressure." },
                    category   = new { type = "string",  @enum = new[] { "fact","preference","pattern","decision","history","note" } },
                    tags       = new { type = "array",   items = new { type = "string" } },
                    visibility = new { type = "string",  @enum = new[] { "private","shared" }, description = "shared also writes .tosh/memories.toml." },
                    scope      = new { type = "string",  @enum = new[] { "project","global" } },
                    source     = new { type = "string",  @enum = new[] { "ai","user" } },
                    session_id = new { type = "string" },
                    pinned     = new { type = "boolean", description = "Pinned memories float to the top of recall/list and surface on initialize." },
                    links      = new { type = "array",   items = LinkSchema, description = "Structured citations (file path + optional line range)." }
                },
                required = new[] { "content", "summary", "category" }
            }
        },
        new
        {
            name = "memory_store_batch",
            description = """
                Store many memories atomically in one call. Each entry uses the
                same fields as memory_store, plus an optional `relate` clause to
                link to a sibling (`to_id: "#0"` references the first result) or
                an existing id. Returns {ids, short_ids, deduped}.
                """,
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    entries = new
                    {
                        type = "array",
                        items = new
                        {
                            type = "object",
                            properties = new
                            {
                                content    = new { type = "string" },
                                summary    = new { type = "string" },
                                category   = new { type = "string",  @enum = new[] { "fact","preference","pattern","decision","history","note" } },
                                tags       = new { type = "array",   items = new { type = "string" } },
                                visibility = new { type = "string",  @enum = new[] { "private","shared" } },
                                scope      = new { type = "string",  @enum = new[] { "project","global" } },
                                pinned     = new { type = "boolean" },
                                links      = new { type = "array",   items = LinkSchema },
                                relate = new
                                {
                                    type = "object",
                                    properties = new
                                    {
                                        to_id        = new { type = "string", description = "Existing id, short_id, or #N to point at sibling N." },
                                        relationship = new { type = "string", @enum = new[] { "supersedes","supports","contradicts","related_to" } }
                                    },
                                    required = new[] { "to_id", "relationship" }
                                }
                            },
                            required = new[] { "content", "summary", "category" }
                        }
                    }
                },
                required = new[] { "entries" }
            }
        },
        new
        {
            name = "memory_update",
            description = """
                Patch an existing memory by id or short_id. Metadata fields
                (summary, tags, scope, visibility, pinned, links) are updated
                in place. Editing `content` is non-destructive: a new row is
                inserted and linked to the old via `supersedes`, then the old
                is tombstoned. Returns {id, short_id} of the resulting row.
                """,
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    id         = new { type = "string", description = "Full id, short_id, or unambiguous prefix (≥4 chars)." },
                    summary    = new { type = "string" },
                    content    = new { type = "string" },
                    tags       = new { type = "array",  items = new { type = "string" } },
                    scope      = new { type = "string", @enum = new[] { "project","global" } },
                    visibility = new { type = "string", @enum = new[] { "private","shared" } },
                    pinned     = new { type = "boolean" },
                    links      = new { type = "array",  items = LinkSchema }
                },
                required = new[] { "id" }
            }
        },
        new
        {
            name = "memory_recall",
            description = """
                FTS5 full-text search with Porter stemming. Returns ranked hits
                (pinned rows boosted). Default `mode` is "snippet" — returns a
                short excerpt around match terms instead of full content. Use
                "brief" for id+summary only (~25 tok/hit), "full" to include
                whole bodies.
                Syntax: quoted phrases, prefix*, boolean AND/OR/NOT.
                Hyphens parse as NOT — quote `"smoke-test"` or split.
                """,
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    query    = new { type = "string" },
                    limit    = new { type = "integer", minimum = 1, maximum = 50 },
                    category = new { type = "string",  @enum = new[] { "fact","preference","pattern","decision","history","note" } },
                    scope    = new { type = "string",  @enum = new[] { "project","global","all" } },
                    tags     = new { type = "array",   items = new { type = "string" } },
                    mode     = new { type = "string",  @enum = new[] { "brief","snippet","full" }, description = "Default 'snippet'." },
                    verbose  = new { type = "boolean", description = "Include created_at, access_count, visibility." },
                    links_path = new { type = "string", description = "Substring filter on cited file paths (post-FTS)." }
                },
                required = new[] { "query" }
            }
        },
        new
        {
            name = "memory_list",
            description = """
                Enumerate memories with filters. Pinned rows appear first.
                Summaries only unless include_content=true. Pass verbose=true
                for full timestamps and access metadata.
                """,
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    category        = new { type = "string",  @enum = new[] { "fact","preference","pattern","decision","history","note" } },
                    scope           = new { type = "string",  @enum = new[] { "project","global","all" } },
                    tags            = new { type = "array",   items = new { type = "string" } },
                    since_session   = new { type = "string" },
                    include_content = new { type = "boolean" },
                    verbose         = new { type = "boolean" },
                    limit           = new { type = "integer", minimum = 1, maximum = 200 },
                    min_age_days    = new { type = "integer", description = "Only rows last accessed ≥N days ago." },
                    max_access_count = new { type = "integer", description = "Only rows accessed ≤N times. Combine with min_age_days to find stale notes." }
                }
            }
        },
        new
        {
            name = "memory_forget",
            description = """
                Soft-delete by id or short_id. User-sourced memories require
                confirm=true. Tombstoned, never physically removed.
                """,
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    id      = new { type = "string", description = "Full id, short_id, or unambiguous prefix." },
                    confirm = new { type = "boolean" },
                    reason  = new { type = "string" }
                },
                required = new[] { "id" }
            }
        },
        new
        {
            name = "memory_relate",
            description = """
                Link memories with a typed relationship. `to_id` may be a
                single id/short_id or an array — useful for "this decision
                supersedes these three older ones" in one call.
                """,
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    from_id      = new { type = "string" },
                    to_id        = new { description = "Single id/short_id, or array of them.",
                                          oneOf = new object[] {
                                              new { type = "string" },
                                              new { type = "array", items = new { type = "string" } }
                                          } },
                    relationship = new { type = "string", @enum = new[] { "supersedes","supports","contradicts","related_to" } }
                },
                required = new[] { "from_id", "to_id", "relationship" }
            }
        },
        new
        {
            name = "memory_tags",
            description = """
                List all distinct tags with usage counts, sorted by count desc.
                Use before storing to avoid inventing tag variants (`tome` vs `Tome`).
                """,
            inputSchema = new { type = "object", properties = new { } }
        },
        new
        {
            name = "memory_graph",
            description = """
                Return a typed-link subgraph around one or more seed memories.
                BFS expands up to `depth` hops (max 3) along memory_relations.
                Optional `relationship` filters edges (e.g. only 'supersedes'
                chains). Returns {nodes, edges} with `content` omitted unless
                include_content=true.
                """,
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    seed = new
                    {
                        description = "Single id/short_id, or array of them.",
                        oneOf = new object[] {
                            new { type = "string" },
                            new { type = "array", items = new { type = "string" } }
                        }
                    },
                    depth = new { type = "integer", minimum = 0, maximum = 3, description = "Hop count from seeds. Default 1." },
                    relationship = new { type = "string", @enum = new[] { "supersedes","supports","contradicts","related_to" } },
                    include_content = new { type = "boolean" }
                },
                required = new[] { "seed" }
            }
        },
        new
        {
            name = "memory_open",
            description = """
                Resolve a memory's structured `links` into clickable file URIs
                (file:// with #Lstart-Lend fragment). Relative paths are
                resolved against `cwd` (default: server CWD). Use this when
                surfacing citations to the user.
                """,
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    id  = new { type = "string", description = "Full id, short_id, or unambiguous prefix." },
                    cwd = new { type = "string", description = "Workspace root for resolving relative paths." }
                },
                required = new[] { "id" }
            }
        }
    ];
}

// ── JsonElement extension helpers ─────────────────────────────────────────────

internal static class JsonElementExtensions
{
    public static string OptString(this JsonElement el, string key, string fallback) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? fallback
            : fallback;

    public static string? OptStringOrNull(this JsonElement el, string key) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    public static int OptInt(this JsonElement el, string key, int fallback) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(key, out var v) && v.TryGetInt32(out var i) ? i : fallback;

    public static bool OptBool(this JsonElement el, string key, bool fallback)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(key, out var v)) return fallback;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => fallback
        };
    }

    public static string[] OptStringArray(this JsonElement el, string key)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(key, out var v) || v.ValueKind != JsonValueKind.Array)
            return [];
        var arr = new List<string>(v.GetArrayLength());
        foreach (var item in v.EnumerateArray())
            if (item.ValueKind == JsonValueKind.String) arr.Add(item.GetString()!);
        return [.. arr];
    }
}
