using System.Text.Json;
using System.Text.Json.Nodes;
using Tosh.DevCompanion.Memory;

namespace Tosh.DevCompanion;

/// <summary>
/// A minimal JSON-RPC / MCP server that exposes the five memory tools.
/// Reads from stdin, writes to stdout — same transport as Tosh.Mcp.
/// </summary>
public sealed class McpMemoryServer(IMemoryStore store)
{
    // JSON-RPC / MCP protocol fields are camelCase by spec (inputSchema,
    // protocolVersion, serverInfo, isError, …). The envelope MUST use these
    // names verbatim or clients silently drop the schema and fail to forward
    // arguments to tool calls.
    private static readonly JsonSerializerOptions EnvelopeOptions = new()
    {
        PropertyNamingPolicy   = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        WriteIndented          = false
    };

    // Tool result payloads (the JSON nested inside `content[].text`) use
    // snake_case so callers can pattern-match on stable, language-agnostic
    // keys (created_at, access_count, relevance_score, …).
    private static readonly JsonSerializerOptions PayloadOptions = new()
    {
        PropertyNamingPolicy   = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        WriteIndented          = false
    };

    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public async Task RunAsync(CancellationToken ct = default)
    {
        using var stdin  = Console.OpenStandardInput();
        using var stdout = Console.OpenStandardOutput();
        var buffer = new byte[1024 * 64];

        while (!ct.IsCancellationRequested)
        {
            var line = await ReadLineAsync(stdin, buffer, ct);
            if (line is null) break;
            if (string.IsNullOrWhiteSpace(line)) continue;

            _ = Task.Run(async () =>
            {
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    await DispatchAsync(doc.RootElement, stdout, ct);
                }
                catch { /* malformed request — ignore */ }
            }, ct);
        }
    }

    // ── Dispatch ──────────────────────────────────────────────────────────────

    private async Task DispatchAsync(JsonElement msg, Stream stdout, CancellationToken ct)
    {
        var id     = msg.TryGetProperty("id",     out var idEl)     ? idEl     : default;
        var method = msg.TryGetProperty("method", out var methodEl) ? methodEl.GetString() : null;
        var @params = msg.TryGetProperty("params", out var paramsEl) ? paramsEl : default;

        object? result;
        try
        {
            result = method switch
            {
                "initialize"  => new { protocolVersion = "2024-11-05", capabilities = new { tools = new { } }, serverInfo = new { name = "tosh-dev-companion", version = "1.0" } },
                "tools/list"  => new { tools = ToolDefinitions },
                "tools/call"  => await HandleToolCallAsync(@params, ct),
                _             => throw new InvalidOperationException($"Unknown method '{method}'.")
            };
        }
        catch (Exception ex)
        {
            result = ErrorContent(ex.Message);
        }

        await WriteResponseAsync(id, result, stdout, ct);
    }

    private async Task<object> HandleToolCallAsync(JsonElement @params, CancellationToken ct)
    {
        var name = @params.GetProperty("name").GetString() ?? string.Empty;
        var args = @params.TryGetProperty("arguments", out var a) ? a : default;

        return name switch
        {
            "memory_store"  => await MemoryStoreAsync(args, ct),
            "memory_recall" => await MemoryRecallAsync(args, ct),
            "memory_list"   => await MemoryListAsync(args, ct),
            "memory_forget" => await MemoryForgetAsync(args, ct),
            "memory_relate" => await MemoryRelateAsync(args, ct),
            _               => ErrorContent($"Unknown tool '{name}'.")
        };
    }

    // ── Tool handlers ─────────────────────────────────────────────────────────

    private async Task<object> MemoryStoreAsync(JsonElement args, CancellationToken ct)
    {
        var req = new StoreRequest(
            Content:    args.GetProperty("content").GetString()!,
            Summary:    args.GetProperty("summary").GetString()!,
            Category:   args.GetProperty("category").GetString()!,
            Source:     args.OptString("source",     "ai"),
            Scope:      args.OptString("scope",      "project"),
            Visibility: args.OptString("visibility", "private"),
            Tags:       args.OptStringArray("tags"),
            SessionId:  args.OptString("session_id", null));

        var entry = await store.StoreAsync(req, ct);
        return OkContent(new
        {
            entry.Id,
            entry.Summary,
            entry.Category,
            entry.Visibility,
            stored_at = entry.CreatedAtUtc.ToString("O")
        });
    }

    private async Task<object> MemoryRecallAsync(JsonElement args, CancellationToken ct)
    {
        var req = new RecallRequest(
            Query:    args.GetProperty("query").GetString()!,
            Limit:    args.OptInt("limit", 10),
            Category: args.OptString("category", null),
            Scope:    args.OptString("scope", "all"),
            Tags:     args.OptStringArray("tags"));

        var result = await store.RecallAsync(req, ct);
        return OkContent(new
        {
            results = result.Results.Select(r => new
            {
                r.Entry.Id,
                r.Entry.Summary,
                content        = r.Entry.Content,
                r.Entry.Category,
                r.Entry.Tags,
                r.Entry.Scope,
                relevance_score = r.RelevanceScore,
                created_at     = r.Entry.CreatedAtUtc.ToString("O"),
                r.Entry.AccessCount
            }),
            total = result.Total,
            query = result.Query
        });
    }

    private async Task<object> MemoryListAsync(JsonElement args, CancellationToken ct)
    {
        var req = new ListRequest(
            Category:       args.OptString("category", null),
            Scope:          args.OptString("scope", "all"),
            Tags:           args.OptStringArray("tags"),
            SinceSession:   args.OptString("since_session", null),
            IncludeContent: args.OptBool("include_content", false),
            Limit:          args.OptInt("limit", 50));

        var entries = await store.ListAsync(req, ct);
        return OkContent(new
        {
            memories = entries.Select(e => new
            {
                e.Id,
                e.Summary,
                content    = req.IncludeContent ? e.Content : null,
                e.Category,
                e.Tags,
                e.Scope,
                e.Visibility,
                created_at = e.CreatedAtUtc.ToString("O"),
                e.AccessCount
            }),
            total = entries.Count
        });
    }

    private async Task<object> MemoryForgetAsync(JsonElement args, CancellationToken ct)
    {
        var req = new ForgetRequest(
            Id:      args.GetProperty("id").GetString()!,
            Confirm: args.OptBool("confirm", false),
            Reason:  args.OptString("reason", null));

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
        var fromId       = args.GetProperty("from_id").GetString()!;
        var toId         = args.GetProperty("to_id").GetString()!;
        var relationship = args.GetProperty("relationship").GetString()!;

        await store.RelateAsync(fromId, toId, relationship, ct);
        return OkContent(new { from_id = fromId, to_id = toId, relationship });
    }

    // ── Transport ─────────────────────────────────────────────────────────────

    private async Task WriteResponseAsync(JsonElement id, object? result, Stream stdout, CancellationToken ct)
    {
        var response = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"]      = JsonNode.Parse(id.ValueKind == JsonValueKind.Undefined ? "null" : id.GetRawText())
        };

        if (result is not null)
            response["result"] = JsonSerializer.SerializeToNode(result, EnvelopeOptions);

        var bytes = System.Text.Encoding.UTF8.GetBytes(response.ToJsonString() + "\n");
        await _writeLock.WaitAsync(ct);
        try   { await stdout.WriteAsync(bytes, ct); await stdout.FlushAsync(ct); }
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

    private static readonly object[] ToolDefinitions =
    [
        new
        {
            name = "memory_store",
            description = """
                Store a memory in the AI companion's persistent memory store.
                Use this when the user states a preference, when an architectural decision
                is made, when a recurring pattern is identified, when a session milestone
                is reached, or when the user explicitly asks to remember something.
                Returns the assigned memory id and a confirmation summary.
                """,
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    content    = new { type = "string",  description = "Full memory content. Be specific — this is what FTS search matches against." },
                    summary    = new { type = "string",  description = "Single-line summary (≤120 chars). Used when injecting context into prompts under token pressure." },
                    category   = new { type = "string",  @enum = new[] { "fact","preference","pattern","decision","history","note" }, description = "fact=project facts; preference=style preferences; pattern=recurring solution; decision=architectural choice; history=session milestone; note=user free text." },
                    tags       = new { type = "array",   items = new { type = "string" }, description = "Optional keyword tags for filtering." },
                    visibility = new { type = "string",  @enum = new[] { "private","shared" }, description = "private=memory.db only (default); shared=also written to .tosh/memories.toml for git-tracking." },
                    scope      = new { type = "string",  @enum = new[] { "project","global" }, description = "project=this repository (default); global=applies across all projects." },
                    source     = new { type = "string",  @enum = new[] { "ai","user" }, description = "Who authored this memory. User-sourced memories are never garbage-collected." },
                    session_id = new { type = "string",  description = "Originating session id for audit trail." }
                },
                required = new[] { "content", "summary", "category" }
            }
        },
        new
        {
            name = "memory_recall",
            description = """
                Search the AI companion memory store using FTS5 full-text search with
                Porter stemming. Returns ranked results ordered by relevance then recency.
                Call this at the start of a session or when context about the project,
                prior decisions, or user preferences would improve your response.
                Also updates access_count and accessed_at on matched rows.
                """,
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    query    = new { type = "string",  description = "Search query. FTS5 syntax supported: quoted phrases, prefix*, boolean AND/OR/NOT." },
                    limit    = new { type = "integer", description = "Maximum results (1..50, default 10).", minimum = 1, maximum = 50 },
                    category = new { type = "string",  @enum = new[] { "fact","preference","pattern","decision","history","note" }, description = "Filter to a single category. Omit to search all." },
                    scope    = new { type = "string",  @enum = new[] { "project","global","all" }, description = "Scope filter. Default 'all'." },
                    tags     = new { type = "array",   items = new { type = "string" }, description = "Require all listed tags on matched memories." }
                },
                required = new[] { "query" }
            }
        },
        new
        {
            name = "memory_list",
            description = """
                Enumerate memories with optional filters. Useful for standup summaries,
                reviewing decisions before a major change, or auditing what the companion
                knows about the project. Returns summaries only unless include_content=true.
                """,
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    category        = new { type = "string",  @enum = new[] { "fact","preference","pattern","decision","history","note" }, description = "Filter by category." },
                    scope           = new { type = "string",  @enum = new[] { "project","global","all" }, description = "Default 'all'." },
                    tags            = new { type = "array",   items = new { type = "string" }, description = "Require all listed tags." },
                    since_session   = new { type = "string",  description = "Return only memories from this session id onward." },
                    include_content = new { type = "boolean", description = "Include full content. Default false — summaries only." },
                    limit           = new { type = "integer", description = "Max results (1..200, default 50).", minimum = 1, maximum = 200 }
                },
                required = Array.Empty<string>()
            }
        },
        new
        {
            name = "memory_forget",
            description = """
                Soft-delete a memory by id. The memory is tombstoned but never physically
                removed, preserving audit history. User-sourced memories (source='user')
                require confirm=true to prevent accidental deletion.
                """,
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    id      = new { type = "string",  description = "UUID of the memory to delete." },
                    confirm = new { type = "boolean", description = "Required when deleting user-sourced memories." },
                    reason  = new { type = "string",  description = "Optional deletion reason stored in the tombstone." }
                },
                required = new[] { "id" }
            }
        },
        new
        {
            name = "memory_relate",
            description = """
                Create a typed directional relationship between two memories (from_id → to_id).
                Use 'supersedes' when a new decision replaces an old one.
                Use 'supports' when evidence reinforces a prior decision.
                Use 'contradicts' to flag tension for human review.
                Use 'related_to' for loose association.
                """,
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    from_id      = new { type = "string", description = "Source memory id." },
                    to_id        = new { type = "string", description = "Target memory id." },
                    relationship = new { type = "string", @enum = new[] { "supersedes","supports","contradicts","related_to" }, description = "Relationship type." }
                },
                required = new[] { "from_id", "to_id", "relationship" }
            }
        }
    ];
}

// ── JsonElement extension helpers ─────────────────────────────────────────────

internal static class JsonElementExtensions
{
    public static string   OptString(this JsonElement el, string key, string? fallback)  =>
        el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? fallback ?? string.Empty
            : fallback ?? string.Empty;

    public static int      OptInt(this JsonElement el, string key, int fallback)          =>
        el.TryGetProperty(key, out var v) && v.TryGetInt32(out var i) ? i : fallback;

    public static bool     OptBool(this JsonElement el, string key, bool fallback)        =>
        el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.True  ? true  :
        el.TryGetProperty(key, out   v)   && v.ValueKind == JsonValueKind.False ? false :
        fallback;

    public static string[] OptStringArray(this JsonElement el, string key)               =>
        el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Array
            ? [.. v.EnumerateArray().Select(e => e.GetString()).Where(s => s is not null).Select(s => s!)]
            : [];
}
