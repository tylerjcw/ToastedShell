using Tosh.Runtime;

namespace Tosh.Stdlib.Shell;

[ShellOnly]
[CommandCategory("Filesystem")]
[CommandExample("forward")]
[CommandOutput("The FileSystemEntry for the next directory.")]
public sealed class ForwardCommand : ShellCommand
{
    public ForwardCommand()
        : base("forward", "Goes forward to the next directory in the stack.", "forward") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var oldDirectory = FileSystemEntry.From(new DirectoryInfo(context.Shell().CurrentDirectory));
        var next = context.Shell().GoForward();

        if (next is null)
        {
            throw new InvalidOperationException("No next directory in the stack.");
        }

        context.Shell().CurrentDirectory = next;
        var newDirectory = FileSystemEntry.From(new DirectoryInfo(next));

        var sender = context.Shell().EventSenderFactory?.Invoke()
            ?? new ShellEventSender(Function: null, Script: null, Line: null);
        var evt = new DirectoryChangedEvent(oldDirectory, newDirectory, sender);
        await context.Shell().Events.RaiseAsync(evt, context.CancellationToken);

        yield return newDirectory;
    }
}
