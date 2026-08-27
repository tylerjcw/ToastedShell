using Tosh.Runtime;

namespace Tosh.Stdlib.Shell;

[ShellOnly]
[CommandCategory("Filesystem")]
[CommandExample("back")]
[CommandOutput("The FileSystemEntry for the previous directory.")]
public sealed class BackCommand : ShellCommand
{
    public BackCommand()
        : base("back", "Goes back to the previous directory in the stack.", "back") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var oldDirectory = FileSystemEntry.From(new DirectoryInfo(context.Shell().CurrentDirectory));
        var previous = context.Shell().GoBack();

        if (previous is null)
        {
            throw new InvalidOperationException("No previous directory in the stack.");
        }

        context.Shell().CurrentDirectory = previous;
        var newDirectory = FileSystemEntry.From(new DirectoryInfo(previous));

        var sender = context.Shell().EventSenderFactory?.Invoke()
            ?? new ShellEventSender(Function: null, Script: null, Line: null);
        var evt = new DirectoryChangedEvent(oldDirectory, newDirectory, sender);
        await context.Shell().Events.RaiseAsync(evt, context.CancellationToken);

        yield return newDirectory;
    }
}
