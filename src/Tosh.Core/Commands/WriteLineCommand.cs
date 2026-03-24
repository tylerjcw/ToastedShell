namespace Tosh.Core.Commands;

public sealed class WriteLineCommand : ShellCommand
{
    public WriteLineCommand()
        : base("writeline", "Writes rendered values with a trailing newline.", "writeline [value...]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var rendered = await WriteCommand.RenderAsync(context);

        if (rendered.Length == 0)
        {
            await context.Runtime.Output.WriteLineAsync();
            yield break;
        }

        await context.Runtime.Output.WriteLineAsync(rendered);
    }
}
