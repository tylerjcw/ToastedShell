namespace Tosh.Core;

public sealed record EventRaiseResult(
    string EventName,
    bool Cancelled,
    int HandlersInvoked,
    IReadOnlyList<object?> Results)
{
    public bool Handled => HandlersInvoked > 0;

    public override string ToString() =>
        Cancelled ? $"[Event: {EventName} — cancelled after {HandlersInvoked} handler(s)]"
                  : $"[Event: {EventName} — {HandlersInvoked} handler(s)]";
}
