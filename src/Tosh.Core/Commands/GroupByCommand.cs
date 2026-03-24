namespace Tosh.Core.Commands;

public sealed class GroupByCommand : ShellCommand
{
    public GroupByCommand()
        : base("group-by", "Groups pipeline values by a member path.", "group-by <member-path>") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var memberPath = CommandArguments.RequireString(context.Arguments, 0, "member path");
        var groups = new Dictionary<string, (object? Key, List<object?> Items)>(StringComparer.Ordinal);

        await foreach (var item in context.Input.WithCancellation(context.CancellationToken))
        {
            object? keyValue;

            try
            {
                keyValue = context.Runtime.ObjectAccessor.GetValue(item, memberPath);
            }
            catch (Exception exception) when (exception is not InvalidOperationException)
            {
                throw new InvalidOperationException($"Could not read member '{memberPath}' for grouping: {exception.Message}");
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
