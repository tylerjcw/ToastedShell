namespace Tosh.Core.Commands;

[CommandCategory("Shell")]
public sealed class ExitCommand : ShellCommand
{
    public ExitCommand()
        : base("exit", "Requests the current Tosh session to exit.", "exit") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        context.Runtime.RequestExit();
        yield break;
    }
}
