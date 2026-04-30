namespace Tosh.Core.Commands;

[Stdlib(StdlibCategory.Text)]
[CommandCategory("Text")]
[CommandArgument("value ...", "Values to render. When omitted, renders pipeline input.", Required = false)]
[CommandExample("writeline \"hello\"", Title = "Write a line")]
[CommandExample("echo alpha beta | writeline", Title = "Write piped values with a newline")]
[CommandOutput("Emits nothing; writes its arguments to stdout (each terminated by a newline) as a side effect.")]
public sealed class WriteLineCommand : ShellCommand, IImplicitGlobCommand
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
