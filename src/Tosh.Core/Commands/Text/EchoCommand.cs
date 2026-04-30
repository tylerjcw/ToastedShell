namespace Tosh.Core.Commands.Text;

[Stdlib(StdlibCategory.Text)]
[CommandCategory("Text")]
[CommandArgument("value", "One or more values to emit as pipeline objects.", Kind = "expression")]
[CommandExample("echo hello world")]
[CommandExample("echo 42 | where { $_ > 10 }")]
[CommandOutput("Each argument as a separate pipeline object.", Mode = "structured")]
public sealed class EchoCommand : ShellCommand, IImplicitGlobCommand
{
    public EchoCommand()
        : base("echo", "Emits its arguments as pipeline objects.", "echo <value> [value...]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        foreach (var argument in context.Arguments)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            yield return argument;
        }
    }
}
