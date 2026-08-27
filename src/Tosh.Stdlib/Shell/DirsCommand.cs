using Tosh.Runtime;

namespace Tosh.Stdlib.Shell;

[ShellOnly]
[CommandCategory("Filesystem")]
[CommandArgument("subcommand", "goto <index>, remove <index>, or clear.", Required = false)]
[CommandExample("dirs")]
[CommandExample("dirs goto 2", Title = "Navigate to stack entry")]
[CommandExample("dirs clear", Title = "Clear the directory stack")]
[CommandOutput("Lists the current directory stack.")]
public sealed class DirsCommand : ShellCommand
{
    public DirsCommand()
        : base("dirs", "Shows or manages the directory stack.", "dirs [goto <index> | remove <index> | clear]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        await Task.CompletedTask;

        var parsed = ParsedCommandArguments.Parse(context.Arguments);

        if (parsed.Positionals.Count == 0)
        {
            foreach (var entry in context.Shell().GetDirectoryStack())
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                yield return entry;
            }

            yield break;
        }

        var action = CommandArguments.RequireString(parsed.Positionals, 0, "action");

        switch (action.ToLowerInvariant())
        {
            case "goto":
                {
                    var index = RequireIndex(parsed, context);
                    var path = context.Shell().GoToStackIndex(index);

                    if (path is null)
                    {
                        throw new InvalidOperationException($"Invalid stack index: {index}.");
                    }

                    context.Shell().CurrentDirectory = path;
                    yield return FileSystemEntry.From(new DirectoryInfo(path));
                    yield break;
                }
            case "remove":
                {
                    var index = RequireIndex(parsed, context);

                    if (!context.Shell().RemoveDirectoryStackEntry(index))
                    {
                        throw new InvalidOperationException(
                            index == context.Shell().DirectoryStackIndex
                                ? "Cannot remove the current directory from the stack."
                                : $"Invalid stack index: {index}.");
                    }

                    foreach (var entry in context.Shell().GetDirectoryStack())
                    {
                        context.CancellationToken.ThrowIfCancellationRequested();
                        yield return entry;
                    }

                    yield break;
                }
            case "clear":
                {
                    context.Shell().ClearDirectoryStack();

                    foreach (var entry in context.Shell().GetDirectoryStack())
                    {
                        yield return entry;
                    }

                    yield break;
                }
            default:
                throw new InvalidOperationException("dirs action must be 'goto', 'remove', or 'clear'.");
        }
    }

    private static int RequireIndex(ParsedCommandArguments parsed, CommandContext context)
    {
        if (parsed.Positionals.Count < 2)
        {
            throw new InvalidOperationException("dirs goto/remove expects a stack index.");
        }

        var value = parsed.Positionals[1];

        return value switch
        {
            int i => i,
            long l => (int)l,
            string s when int.TryParse(s, out var result) => result,
            _ => throw new InvalidOperationException("Stack index must be an integer."),
        };
    }
}
