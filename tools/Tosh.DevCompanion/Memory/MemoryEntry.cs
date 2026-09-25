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

    // Stable short form: the last 8 hex chars of the UUID, which are random. The first 8 of
    // a UUIDv7 are the top of its millisecond timestamp, shared by every memory created in the
    // same ~65 seconds, so a prefix short id was ambiguous for memories stored together.
    public string ShortId => Id.Length >= 8 ? Id[^8..] : Id;
}
