namespace Tosh.DevCompanion.Memory;

public interface IMemoryStore : IDisposable
{
    Task<StoreResult> StoreAsync(StoreRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<StoreResult>> StoreBatchAsync(IReadOnlyList<StoreRequest> requests, CancellationToken ct = default);
    Task<MemoryEntry> UpdateAsync(UpdateRequest request, CancellationToken ct = default);
    Task<RecallResult> RecallAsync(RecallRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<MemoryEntry>> ListAsync(ListRequest request, CancellationToken ct = default);
    Task<MemoryEntry?> GetAsync(string id, CancellationToken ct = default);
    Task<string?> ResolveIdAsync(string idOrPrefix, CancellationToken ct = default);
    Task<bool> ForgetAsync(ForgetRequest request, CancellationToken ct = default);
    Task RelateAsync(string fromId, string toId, string relationship, CancellationToken ct = default);
    Task<IReadOnlyList<MemoryRelation>> GetRelationsAsync(string id, CancellationToken ct = default);
    Task<IReadOnlyList<TagCount>> GetTagsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<MemoryEntry>> GetPinnedAsync(string scope, int limit, CancellationToken ct = default);
    Task<MemoryGraph> GetGraphAsync(GraphRequest request, CancellationToken ct = default);
}

// ── Requests ──────────────────────────────────────────────────────────────────

public sealed record StoreRequest(
    string Content,
    string Summary,
    string Category,
    string Source = "ai",
    string Scope = "project",
    string Visibility = "private",
    string[] Tags = default!,
    string? SessionId = null,
    bool Pinned = false,
    MemoryLink[] Links = default!)
{
    public string[] Tags { get; init; } = Tags ?? [];
    public MemoryLink[] Links { get; init; } = Links ?? [];
}

public sealed record MemoryLink(
    string Path,
    int? Line = null,
    int? LineEnd = null,
    string? Kind = null);

public sealed record StoreResult(
    MemoryEntry Entry,
    bool Deduped);

public sealed record UpdateRequest(
    string Id,
    string? Summary = null,
    string? Content = null,
    string[]? Tags = null,
    string? Scope = null,
    string? Visibility = null,
    bool? Pinned = null,
    MemoryLink[]? Links = null);

public sealed record TagCount(
    string Tag,
    int Count);

public sealed record RecallRequest(
    string Query,
    int Limit = 10,
    string? Category = null,
    string Scope = "all",
    string[] Tags = default!,
    string Mode = "snippet",   // "brief" | "snippet" | "full"
    bool Verbose = false,
    string? LinksPath = null)
{
    public string[] Tags { get; init; } = Tags ?? [];
}

public sealed record ListRequest(
    string? Category = null,
    string Scope = "all",
    string[] Tags = default!,
    string? SinceSession = null,
    bool IncludeContent = false,
    int Limit = 50,
    int? MinAgeDays = null,
    int? MaxAccessCount = null)
{
    public string[] Tags { get; init; } = Tags ?? [];
}

public sealed record GraphRequest(
    IReadOnlyList<string> Seeds,
    int Depth = 1,
    string? Relationship = null,
    bool IncludeContent = false);

public sealed record ForgetRequest(
    string Id,
    bool Confirm = false,
    string? Reason = null);

// ── Results ───────────────────────────────────────────────────────────────────

public sealed record RecallResult(
    IReadOnlyList<ScoredMemory> Results,
    int Total,
    string Query);

public sealed record ScoredMemory(
    MemoryEntry Entry,
    double RelevanceScore,
    string? Snippet = null);

public sealed record MemoryGraph(
    IReadOnlyList<MemoryEntry> Nodes,
    IReadOnlyList<MemoryRelation> Edges);
