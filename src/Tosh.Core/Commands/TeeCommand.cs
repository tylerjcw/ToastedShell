using System.Text;

namespace Tosh.Core.Commands;

[Stdlib(StdlibCategory.Pipeline)]
[CommandCategory("Pipeline")]
[CommandArgument("path", "Optional file path to write a copy of the stream to.", Required = false, TypeName = "path-like")]
[CommandOption("-a, --append", "Append to the output file instead of replacing it.")]
[CommandOption("-v, --variable <name>", "Capture the stream into a shell variable while still passing values through.")]
[CommandExample("ls | tee snapshot.txt | where _.IsDirectory", Title = "Write a copy to a file and keep piping")]
[CommandExample("ps | tee -v processes | count", Title = "Capture a pipeline into a variable")]
[CommandOutput("Re-emits each input item unchanged, while also writing it to the configured destination as a side effect.")]
public sealed class TeeCommand : ShellCommand
{
    public TeeCommand()
        : base("tee", "Passes values through while also writing them out or capturing them.", "tee [-a] [path] or tee -v <name>") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var parsed = ParsedCommandArguments.Parse(context.Arguments);
        var captureVariable = parsed.HasFlag("v", "variable");
        var append = parsed.HasFlag("a", "append");

        if (captureVariable)
        {
            if (parsed.Positionals.Count != 1)
            {
                throw new InvalidOperationException("tee -v expects exactly one variable name.");
            }

            var name = CommandArguments.RequireString(parsed.Positionals, 0, "variable name");
            var captured = new List<object?>();

            await foreach (var item in context.Input.WithCancellation(context.CancellationToken))
            {
                captured.Add(item);
                yield return item;
            }

            context.Runtime.Variables[name] = captured.Count switch
            {
                0 => null,
                1 => captured[0],
                _ => captured.ToArray(),
            };

            yield break;
        }

        StreamWriter? writer = null;

        try
        {
            if (parsed.Positionals.Count == 1)
            {
                var path = PathUtilities.ResolvePath(context.Runtime.CurrentDirectory, CommandArguments.RequireString(parsed.Positionals, 0, "path"));
                var mode = append ? FileMode.Append : FileMode.Create;
                writer = new StreamWriter(File.Open(path, mode, FileAccess.Write, FileShare.Read), Encoding.UTF8);
            }
            else if (parsed.Positionals.Count > 1)
            {
                throw new InvalidOperationException("tee accepts at most one output path.");
            }

            await foreach (var item in context.Input.WithCancellation(context.CancellationToken))
            {
                var text = item switch
                {
                    ShellTextLine line => line.Text,
                    _ => context.Runtime.Formatter.Format(item),
                };

                if (writer is not null)
                {
                    await writer.WriteLineAsync(text);
                    await writer.FlushAsync(context.CancellationToken);
                }
                else
                {
                    await context.Runtime.Output.WriteLineAsync(text);
                }

                yield return item;
            }
        }
        finally
        {
            if (writer is not null)
            {
                await writer.DisposeAsync();
            }
        }
    }
}
