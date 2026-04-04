namespace Tosh.Core;

public sealed record CommandHistoryEntry(long Id, string Text, DateTimeOffset Timestamp)
{
    public long Index => Id;

    public DateTimeOffset When => Timestamp;
}
