using Tosh.Runtime;

namespace Tosh.Stdlib.Shell;

[CommandCategory("Shell")]
[CommandArgument("prompt", "Optional prompt text to print before reading.", Required = false)]
[CommandExample("read-line \"Name: \"", Title = "Prompt for one line")]
[CommandExample("var answer = (read-line \"Continue? \")", Title = "Capture input in a variable")]
[CommandOutput("A single string containing the line read from stdin (or null on EOF).", ClrType = typeof(string))]
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
            await context.Shell().Output.WriteAsync(prompt);
            await context.Shell().Output.FlushAsync(context.CancellationToken);
        }

        var line = Console.ReadLine();

        if (line is not null)
        {
            yield return line;
        }
    }
}
