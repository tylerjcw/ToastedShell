namespace Tosh.Core;

public class ShellEvent : IShellRecordObject
{
    private bool _cancelled;

    public ShellEvent(string name, ShellEventSender sender)
    {
        Name = name;
        Sender = sender;
        Timestamp = DateTimeOffset.Now;
    }

    public string Name { get; }

    public ShellEventSender Sender { get; internal set; }

    public DateTimeOffset Timestamp { get; }

    public bool Cancelled => _cancelled;

    public string ShellTypeName => Name;

    public void Cancel()
    {
        _cancelled = true;
    }

    public virtual bool TryGetMember(string name, out object? value, bool includeHidden = false)
    {
        value = name switch
        {
            "Name" or "name" => Name,
            "Sender" or "sender" => Sender,
            "Timestamp" or "timestamp" => Timestamp,
            "Cancelled" or "cancelled" => Cancelled,
            _ => null,
        };

        return value is not null || name is "Name" or "name" or "Sender" or "sender" or "Timestamp" or "timestamp" or "Cancelled" or "cancelled";
    }

    public virtual bool TrySetMember(string name, object? value) => false;

    public virtual IReadOnlyList<KeyValuePair<string, object?>> GetMembers(bool includeHidden = false)
    {
        return
        [
            new("Name", Name),
            new("Sender", Sender),
            new("Timestamp", Timestamp),
            new("Cancelled", Cancelled),
        ];
    }

    public override string ToString() => $"[Event: {Name}]";
}
