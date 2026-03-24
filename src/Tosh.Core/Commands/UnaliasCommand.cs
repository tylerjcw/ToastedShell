namespace Tosh.Core.Commands;

public sealed class UnaliasCommand : ShellCommand
{
    public UnaliasCommand()
        : base("unalias", "Removes user-defined aliases.", "unalias <name> [name...]") { }

    public override IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        return AsyncEnumerableExtensions.FromEnumerable(RemoveByKind(context, CommandResolutionKind.Alias));
    }

    private static IReadOnlyList<object?> RemoveByKind(CommandContext context, CommandResolutionKind kind)
    {
        var results = new List<object?>();

        foreach (var name in context.Arguments.Select((_, index) => CommandArguments.RequireString(context.Arguments, index, "name")))
        {
            var removed = context.Runtime.Commands.TryGet(name, out var command) &&
                          command is ICommandResolutionMetadata metadata &&
                          metadata.ResolutionKind == kind &&
                          context.Runtime.Commands.Remove(name);

            results.Add(new ProjectedObject(
            [
                new ProjectedField("Name", "Name", name),
                new ProjectedField("Removed", "Removed", removed),
            ]));
        }

        return results;
    }
}
