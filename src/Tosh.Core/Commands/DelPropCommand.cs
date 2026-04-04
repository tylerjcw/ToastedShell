namespace Tosh.Core.Commands;

public sealed class DelPropCommand : ShellCommand
{
    public DelPropCommand()
        : base("del-prop", "Removes a property from a dynamic record.", "del-prop [object] <name>") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var (target, name) = await ResolveArguments(context);

        if (target is null)
        {
            throw new InvalidOperationException("del-prop requires an object. Usage: del-prop [object] <name>");
        }

        if (target is IDictionary<string, object?> dictionary)
        {
            var existingKey = dictionary.Keys.FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));

            if (existingKey is not null)
            {
                dictionary.Remove(existingKey);
            }

            yield return target;
            yield break;
        }

        throw new InvalidOperationException($"Cannot remove properties from {target.GetType().Name}. del-prop only works on dynamic records.");
    }

    private static async Task<(object? Target, string Name)> ResolveArguments(CommandContext context)
    {
        if (context.Arguments.Count >= 2)
        {
            return (context.Arguments[0], context.Arguments[1]?.ToString() ?? string.Empty);
        }

        if (context.Arguments.Count == 1)
        {
            var items = await AsyncEnumerableExtensions.ToListAsync(context.Input, context.CancellationToken);
            return (items.Count > 0 ? items[0] : null, context.Arguments[0]?.ToString() ?? string.Empty);
        }

        throw new InvalidOperationException("del-prop requires a property name. Usage: del-prop [object] <name>");
    }
}
