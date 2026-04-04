namespace Tosh.Core;

public sealed class ShellEventHandler
{
    public ShellEventHandler(
        string eventName,
        string handlerName,
        Func<ShellEvent, CancellationToken, Task<object?>> handler,
        int? priority = null,
        bool once = false,
        IReadOnlyList<object>? capturedScopes = null)
    {
        EventName = eventName;
        HandlerName = handlerName;
        Handler = handler;
        Priority = priority;
        Once = once;
        CapturedScopes = capturedScopes;
        RegistrationOrder = Interlocked.Increment(ref _nextRegistrationOrder);
    }

    private static long _nextRegistrationOrder;

    public string EventName { get; }

    public string HandlerName { get; }

    public Func<ShellEvent, CancellationToken, Task<object?>> Handler { get; }

    public int? Priority { get; }

    public bool Once { get; }

    public IReadOnlyList<object>? CapturedScopes { get; }

    public long RegistrationOrder { get; }

    public override string ToString() => Priority is int p
        ? $"{HandlerName} handles {EventName} priority {p}{(Once ? " once" : "")}"
        : $"{HandlerName} handles {EventName}{(Once ? " once" : "")}";
}
