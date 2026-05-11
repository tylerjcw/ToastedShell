namespace Tosh.DevCompanion.Memory;

public interface IMemoryStore : IDisposable
{
    Task<MemoryEntry>          StoreAsync(StoreRequest request, CancellationToken ct = default);
    Task<RecallResult>         RecallAsync(RecallRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<MemoryEntry>> ListAsync(ListRequest request, CancellationToken ct = default);
    Task<MemoryEntry?>         GetAsync(string id, CancellationToken ct = default);
    Task<bool>                 ForgetAsync(ForgetRequest request, CancellationToken ct = default);
    Task                       RelateAsync(string fromId, string toId, string relationship, CancellationToken ct = default);
    Task<IReadOnlyList<MemoryRelation>> GetRelationsAsync(string id, CancellationToken ct = default);
}

// ── Requests ──────────────────────────────────────────────────────────────────

public sealed record StoreRequest(
    string   Content,
    string   Summary,
    string   Category,
    string   Source      = "ai",
    string   Scope       = "project",
    string   Visibility  = "private",
    string[] Tags        = default!,
    string?  SessionId   = null)
{
    public string[] Tags { get; init; } = Tags ?? [];
}

public sealed record RecallRequest(
    string   Query,
    int      Limit       = 10,
    string?  Category    = null,
    string   Scope       = "all",
    string[] Tags        = default!)
{
    public string[] Tags { get; init; } = Tags ?? [];
}

public sealed record ListRequest(
    string?  Category      = null,
    string   Scope         = "all",
    string[] Tags          = default!,
    string?  SinceSession  = null,
    bool     IncludeContent = false,
    int      Limit         = 50)
{
    public string[] Tags { get; init; } = Tags ?? [];
}

public sealed record ForgetRequest(
    string  Id,
    bool    Confirm = false,
    string? Reason  = null);

// ── Results ───────────────────────────────────────────────────────────────────

public sealed record RecallResult(
    IReadOnlyList<ScoredMemory> Results,
    int                         Total,
    string                      Query);

public sealed record ScoredMemory(
    MemoryEntry Entry,
    double      RelevanceScore);
