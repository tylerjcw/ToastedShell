using Tosh.Runtime;

namespace Tosh.Stdlib.Pipeline;

[CommandCategory("Pipeline")]
[CommandExample("ls | group-by Extension")]
[CommandExample("ps | group-by func(p) => ($p.Name.Substring(0, 1))")]
[CommandOutput("Group records of the form { Key, Items } — one per distinct key produced by the projection.")]
public sealed class GroupByCommand : ShellCommand, ICurrentItemMemberPathCommand
{
    public GroupByCommand()
        : base("group-by", "Groups pipeline values by a member path, block, or callable.", "group-by <member-path|callable|block>") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (context.Arguments.Count != 1)
        {
            throw context.CreateDiagnostic(
                code: "tosh.runtime.group_by_requires_selector",
                title: "'group-by' requires exactly one member path, callable, or block.",
                label: "pass a member path like 'Name', a lambda, or a block");
        }

        var selector = context.Arguments[0];
        var groups = new Dictionary<string, (object? Key, List<object?> Items)>(StringComparer.Ordinal);

        await foreach (var item in ShellIterationUtilities.ReplaySingleInputCollectionAsync(context.Input, context.CancellationToken)
                           .WithCancellation(context.CancellationToken))
        {
            object? keyValue;

            if (selector is IShellCallable or ShellBlock)
            {
                keyValue = await FunctionalCommandUtilities.RequireSingleResultAsync(
                    context,
                    selector,
                    [item],
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["_"] = item,
                    });
            }
            else
            {
                var memberPath = CommandArguments.RequireString(context.Arguments, 0, "member path");

                try
                {
                    keyValue = context.Runtime.ObjectAccessor.GetValue(item, memberPath);
                }
                catch (Exception exception) when (exception is not InvalidOperationException)
                {
                    throw new InvalidOperationException($"Could not read member '{memberPath}' for grouping: {exception.Message}");
                }
            }

            var key = ShellDataSerializer.GetStableKey(keyValue) ?? string.Empty;

            if (!groups.TryGetValue(key, out var entry))
            {
                entry = (keyValue, []);
                groups[key] = entry;
            }

            entry.Items.Add(item);
        }

        foreach (var group in groups.Values)
        {
            yield return new GroupingInfo(group.Key, group.Items.ToArray());
        }
    }
}
