namespace Tosh.Core.Commands;

public sealed class ReadLineCommand : ShellCommand
{
    public ReadLineCommand()
        : base("read-line", "Reads a line of text from standard input.", "read-line [prompt]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        string? prompt = null;

        if (context.Arguments.Count > 0)
        {
            prompt = context.Arguments[0]?.ToString();
        }

        if (prompt is not null)
        {
            await context.Runtime.Output.WriteAsync(prompt);
            await context.Runtime.Output.FlushAsync(context.CancellationToken);
        }

        var line = Console.ReadLine();

        if (line is not null)
        {
            yield return line;
        }
    }
}
