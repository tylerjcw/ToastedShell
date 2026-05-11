namespace Tosh.DevCompanion.Memory;

public sealed record MemoryRelation(
    string FromId,
    string ToId,
    string Relationship,
    long CreatedAt);
