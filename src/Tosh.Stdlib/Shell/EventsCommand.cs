using Tosh.Runtime;

namespace Tosh.Stdlib.Shell;

[Stdlib(StdlibCategory.Shell)]
[CommandCategory("Shell")]
[CommandArgument("action", "The action to perform: list, names, handlers, remove, or clear.", Required = false)]
[CommandArgument("event-name", "The event name (for handlers, remove, clear).", Required = false)]
[CommandArgument("handler", "The handler to remove (for remove action).", Required = false)]
[CommandExample("events", Title = "List all registered events")]
[CommandExample("events names", Title = "List event names only")]
[CommandExample("events handlers OnPrompt", Title = "List handlers for an event")]
[CommandExample("events clear OnPrompt", Title = "Remove all handlers for an event")]
[CommandOutput("Event descriptors, names, or handler objects depending on the action.")]
public sealed class EventsCommand : ShellCommand
{
    public EventsCommand()
        : base("events", "Lists, inspects, and manages event handlers.", "events [list|names|handlers <event>|remove <event> <handler>|clear <event>]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        await Task.CompletedTask;

        var parsed = ParsedCommandArguments.Parse(context.Arguments);

        if (parsed.Positionals.Count == 0)
        {
            foreach (var handler in context.Runtime.Events.GetHandlers())
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                yield return handler;
            }

            yield break;
        }

        var action = CommandArguments.RequireString(parsed.Positionals, 0, "action");

        switch (action.ToLowerInvariant())
        {
            case "list":
                foreach (var handler in context.Runtime.Events.GetHandlers())
                {
                    context.CancellationToken.ThrowIfCancellationRequested();
                    yield return handler;
                }

                yield break;

            case "names":
                foreach (var name in context.Runtime.Events.GetRegisteredEventNames())
                {
                    context.CancellationToken.ThrowIfCancellationRequested();
                    yield return name;
                }

                yield break;

            case "handlers":
                {
                    var eventName = CommandArguments.RequireString(parsed.Positionals, 1, "event name");

                    foreach (var handler in context.Runtime.Events.GetHandlers(eventName))
                    {
                        context.CancellationToken.ThrowIfCancellationRequested();
                        yield return handler;
                    }

                    yield break;
                }

            case "remove":
                {
                    var eventName = CommandArguments.RequireString(parsed.Positionals, 1, "event name");
                    var handlerName = CommandArguments.RequireString(parsed.Positionals, 2, "handler name");

                    if (!context.Runtime.Events.Remove(eventName, handlerName))
                    {
                        throw new InvalidOperationException($"No handler '{handlerName}' found for event '{eventName}'.");
                    }

                    yield return new EventHandlerRemovalResult(eventName, handlerName);
                    yield break;
                }

            case "clear":
                {
                    var eventName = CommandArguments.RequireString(parsed.Positionals, 1, "event name");
                    var count = context.Runtime.Events.RemoveAll(eventName);
                    yield return new EventClearResult(eventName, count);
                    yield break;
                }

            default:
                throw new InvalidOperationException("events action must be 'list', 'names', 'handlers', 'remove', or 'clear'.");
        }
    }
}

public sealed record EventHandlerRemovalResult(string EventName, string HandlerName);

public sealed record EventClearResult(string EventName, int HandlersRemoved);
