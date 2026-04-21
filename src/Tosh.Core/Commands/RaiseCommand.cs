namespace Tosh.Core.Commands;

[CommandCategory("Scripting")]
[CommandArgument("event", "Event object, event factory, or event type to raise. May also be supplied from the pipeline.", Required = false)]
[CommandArgument("fields", "Optional record of field overrides when raising from an event factory.", Required = false)]
[CommandExample("$event | raise", Title = "Raise a piped event")]
[CommandExample("raise $factory { Name: \"deploy\", Status: \"ok\" }", Title = "Raise from an event factory with fields")]
public sealed class RaiseCommand : ShellCommand
{
    public RaiseCommand()
        : base("raise", "Raises an event, invoking all registered handlers.", "raise <event> | <event> | raise") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var parsed = ParsedCommandArguments.Parse(context.Arguments);
        ShellEvent? shellEvent = null;

        if (parsed.Positionals.Count > 0)
        {
            IDictionary<string, object?>? fieldOverrides = parsed.Positionals.Count > 1
                ? parsed.Positionals[1] as IDictionary<string, object?>
                : null;

            shellEvent = ResolveEvent(parsed.Positionals[0], context, fieldOverrides);
        }

        if (shellEvent is null)
        {
            await foreach (var item in context.Input.WithCancellation(context.CancellationToken))
            {
                shellEvent = ResolveEvent(item, context);

                if (shellEvent is not null)
                {
                    break;
                }
            }
        }

        if (shellEvent is null)
        {
            throw new InvalidOperationException("raise expects an event object or event type. Usage: raise <event> or <event> | raise");
        }

        var result = await context.Runtime.Events.RaiseAsync(shellEvent, context.CancellationToken);
        yield return result;
    }

    private static ShellEvent? ResolveEvent(object? value, CommandContext context, IDictionary<string, object?>? fieldOverrides = null)
    {
        var sender = context.Runtime.EventSenderFactory?.Invoke()
            ?? new ShellEventSender(Function: null, Script: null, Line: null);

        return value switch
        {
            ShellEvent evt => InjectSender(evt, sender),
            IShellEventFactory factory when fieldOverrides is not null =>
                factory.CreateEvent(sender, fieldOverrides.ToList()),
            IShellEventFactory factory => factory.CreateEvent(sender),
            _ => null,
        };
    }

    private static ShellEvent InjectSender(ShellEvent evt, ShellEventSender sender)
    {
        if (evt.Sender.Function is null && evt.Sender.Script is null)
        {
            evt.Sender = sender;
        }

        return evt;
    }
}
