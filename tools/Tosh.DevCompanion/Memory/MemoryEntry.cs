namespace Tosh.DevCompanion.Memory;

public sealed record MemoryEntry(
    string Id,
    string Content,
    string Summary,
    string Tags,
    string Category,
    string Source,
    string Scope,
    string Visibility,
    string? SessionId,
    long CreatedAt,
    long AccessedAt,
    int AccessCount,
    bool IsDeleted,
    long? DeletedAt,
    bool Pinned = false,
    string? LinksJson = null)
{
    public DateTimeOffset CreatedAtUtc => DateTimeOffset.FromUnixTimeMilliseconds(CreatedAt);
    public DateTimeOffset AccessedAtUtc => DateTimeOffset.FromUnixTimeMilliseconds(AccessedAt);
    public string[] TagList => Tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    // Stable short form: first 8 hex chars of the UUID. UUIDv7 puts the
    // timestamp in the leading bytes, so prefixes stay collision-resistant
    // across normal usage; the resolver falls back to a uniqueness check.
    public string ShortId => Id.Length >= 8 ? Id[..8] : Id;
}
