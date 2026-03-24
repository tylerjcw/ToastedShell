namespace Tosh.Core.Commands;

public sealed class EchoCommand : ShellCommand
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
