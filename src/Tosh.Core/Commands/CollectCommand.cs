namespace Tosh.Core.Commands;

public sealed class CollectCommand : ShellCommand
{
    public CollectCommand()
        : base("collect", "Collects all pipeline items into a single list.", "collect") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var items = await AsyncEnumerableExtensions.ToListAsync(context.Input, context.CancellationToken);
        yield return items.ToArray();
    }
}
