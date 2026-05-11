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
    long? DeletedAt)
{
    public DateTimeOffset CreatedAtUtc   => DateTimeOffset.FromUnixTimeMilliseconds(CreatedAt);
    public DateTimeOffset AccessedAtUtc  => DateTimeOffset.FromUnixTimeMilliseconds(AccessedAt);
    public string[]       TagList        => Tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
