namespace Tosh.Core.Commands;

[Stdlib(StdlibCategory.Scripting)]
[CommandCategory("Scripting")]
[CommandArgument("name", "One or more function names to remove.")]
[CommandExample("undef myfunction", Title = "Remove a user-defined function")]
[CommandExample("undef greet parse transform", Title = "Remove multiple functions at once")]
[CommandOutput("Records with Name and Removed properties indicating which functions were removed.")]
public sealed class UndefCommand : ShellCommand
{
    public UndefCommand()
        : base("undef", "Removes user-defined functions.", "undef <name> [name...]") { }

    public override IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var results = new List<object?>();

        foreach (var name in context.Arguments.Select((_, index) => CommandArguments.RequireString(context.Arguments, index, "name")))
        {
            var removed = context.Runtime.Commands.TryGet(name, out var command) &&
                          command is ICommandResolutionMetadata metadata &&
                          metadata.ResolutionKind == CommandResolutionKind.Function &&
                          context.Runtime.Commands.Remove(name);

            results.Add(ShellRecordUtilities.CreateExpando(
            [
                new KeyValuePair<string, object?>("Name", name),
                new KeyValuePair<string, object?>("Removed", removed),
            ]));
        }

        return AsyncEnumerableExtensions.FromEnumerable(results);
    }
}
