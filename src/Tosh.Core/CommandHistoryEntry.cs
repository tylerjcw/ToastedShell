namespace Tosh.Core;

public sealed record CommandHistoryEntry(int Index, string Text, DateTimeOffset Timestamp)
{
    public DateTimeOffset When => Timestamp;
}
