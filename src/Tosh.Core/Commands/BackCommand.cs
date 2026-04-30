namespace Tosh.Core.Commands;

[ShellOnly]
[Stdlib(StdlibCategory.Shell)]
[CommandCategory("Filesystem")]
[CommandExample("back")]
[CommandOutput("The FileSystemEntry for the previous directory.")]
public sealed class BackCommand : ShellCommand
{
    public BackCommand()
        : base("back", "Goes back to the previous directory in the stack.", "back") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var oldDirectory = FileSystemEntry.From(new DirectoryInfo(context.Runtime.CurrentDirectory));
        var previous = context.Runtime.GoBack();

        if (previous is null)
        {
            throw new InvalidOperationException("No previous directory in the stack.");
        }

        context.Runtime.CurrentDirectory = previous;
        var newDirectory = FileSystemEntry.From(new DirectoryInfo(previous));

        var sender = context.Runtime.EventSenderFactory?.Invoke()
            ?? new ShellEventSender(Function: null, Script: null, Line: null);
        var evt = new DirectoryChangedEvent(oldDirectory, newDirectory, sender);
        await context.Runtime.Events.RaiseAsync(evt, context.CancellationToken);

        yield return newDirectory;
    }
}
