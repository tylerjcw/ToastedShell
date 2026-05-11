using Tosh.DevCompanion;
using Tosh.DevCompanion.Memory;

// ── Mode selection ────────────────────────────────────────────────────────────
//
//  --mcp (or no args)  Start the MCP server (stdin/stdout JSON-RPC).
//  recall <query>      Search memories and print JSON to stdout.
//  list                List memories and print JSON to stdout.
//  store <text>        Store a new user memory.
//  forget <id>         Soft-delete a memory by id.
//
// DB path resolution order:
//   1. TOSH_MEMORY_DB env var
//   2. .tosh/memory.db  (project-local, relative to CWD)
//   3. ~/.tosh/memory.db (global fallback)

var dbPath = ResolveDbPath();
using var store = await SqliteMemoryStore.OpenAsync(dbPath);

if (args is ["--mcp"] or [])
{
    var server = new McpMemoryServer(store);
    await server.RunAsync();
    return 0;
}

return await RunCliAsync(args, store);

// ── CLI mode ──────────────────────────────────────────────────────────────────

static async Task<int> RunCliAsync(string[] args, IMemoryStore store)
{
    var json = new System.Text.Json.JsonSerializerOptions
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower,
        WriteIndented        = true
    };

    var cmd  = args[0];
    var rest = args[1..];

    switch (cmd)
    {
        case "recall":
        {
            var query = string.Join(' ', GetPositional(rest));
            if (string.IsNullOrWhiteSpace(query)) { Console.Error.WriteLine("Usage: recall <query>"); return 1; }
            var limitStr = GetFlag(rest, "--limit");
            var limit    = limitStr is not null && int.TryParse(limitStr, out var l) ? l : 20;
            var category = GetFlag(rest, "--category");
            var scope    = GetFlag(rest, "--scope") ?? "all";
            var result   = await store.RecallAsync(new RecallRequest(query, Limit: limit, Category: category, Scope: scope));
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(result, json));
            return 0;
        }
        case "list":
        {
            var category = GetFlag(rest, "--category");
            var scope    = GetFlag(rest, "--scope") ?? "all";
            var limitStr = GetFlag(rest, "--limit");
            var limit    = limitStr is not null && int.TryParse(limitStr, out var l) ? l : 50;
            var entries  = await store.ListAsync(new ListRequest(Category: category, Scope: scope, IncludeContent: HasFlag(rest, "--full"), Limit: limit));
            var wrapped  = new { total = entries.Count, memories = entries };
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(wrapped, json));
            return 0;
        }
        case "store":
        {
            var content    = GetFlag(rest, "--content")    ?? string.Join(' ', rest.Where(a => !a.StartsWith("--")));
            var summary    = GetFlag(rest, "--summary")    ?? content[..Math.Min(content.Length, 100)];
            var category   = GetFlag(rest, "--category")   ?? "note";
            var tags       = (GetFlag(rest, "--tags") ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries);
            var visibility = GetFlag(rest, "--visibility") ?? "private";
            var scope      = GetFlag(rest, "--scope")      ?? "project";

            var entry = await store.StoreAsync(new StoreRequest(
                Content: content, Summary: summary, Category: category,
                Source: "user", Tags: tags, Visibility: visibility, Scope: scope));
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(entry, json));
            return 0;
        }
        case "forget":
        {
            if (rest.Length == 0) { Console.Error.WriteLine("Usage: forget <id> [--confirm]"); return 1; }
            var deleted = await store.ForgetAsync(new ForgetRequest(rest[0], Confirm: HasFlag(rest, "--confirm")));
            Console.WriteLine(deleted ? "deleted" : "not found");
            return 0;
        }
        default:
            Console.Error.WriteLine($"Unknown command '{cmd}'. Use --mcp, recall, list, store, or forget.");
            return 1;
    }
}

// ── Helpers ───────────────────────────────────────────────────────────────────

static string ResolveDbPath()
{
    var env = Environment.GetEnvironmentVariable("TOSH_MEMORY_DB");
    if (!string.IsNullOrWhiteSpace(env)) return env;

    var local = Path.Combine(Directory.GetCurrentDirectory(), ".tosh", "memory.db");
    if (File.Exists(local)) return local;

    return Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".tosh", "memory.db");
}

static string? GetFlag(string[] args, string flag)
{
    var idx = Array.IndexOf(args, flag);
    return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : null;
}

static bool HasFlag(string[] args, string flag) => Array.IndexOf(args, flag) >= 0;

// Returns args that are not --flag names or their values.
static IEnumerable<string> GetPositional(string[] args)
{
    var i = 0;
    while (i < args.Length)
    {
        if (args[i].StartsWith("--", StringComparison.Ordinal))
        {
            i += 2; // skip --flag and its value
        }
        else
        {
            yield return args[i++];
        }
    }
}
