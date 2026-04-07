namespace Tosh.Core.Commands;

[CommandCategory("Scripting")]
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
