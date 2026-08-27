using Tosh.Runtime;

namespace Tosh.Stdlib.Shell;

[CommandCategory("Shell")]
[CommandExample("clear", Title = "Clear the terminal")]
[CommandOutput("Emits nothing; clears the terminal display as a side effect.")]
public sealed class ClearCommand : ShellCommand
{
    public ClearCommand()
        : base("clear", "Clears the terminal display.", "clear") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        await context.Shell().Output.WriteAsync("\u001b[2J\u001b[H");
        await context.Shell().Output.FlushAsync();
        yield break;
    }
}
