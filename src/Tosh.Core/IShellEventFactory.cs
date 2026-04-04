namespace Tosh.Core;

public interface IShellEventFactory
{
    string EventName { get; }

    ShellEvent CreateEvent(ShellEventSender sender);

    ShellEvent CreateEvent(ShellEventSender sender, IReadOnlyList<KeyValuePair<string, object?>> fieldOverrides) =>
        CreateEvent(sender);
}
