namespace Tosh.Core.Commands;

public sealed class DistinctCommand : ShellCommand
{
    public DistinctCommand()
        : base("distinct", "Removes duplicate pipeline values.", "distinct [member-path]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        string? memberPath = null;

        if (context.Arguments.Count > 1)
        {
            throw new InvalidOperationException("distinct accepts at most one member path.");
        }

        if (context.Arguments.Count == 1)
        {
            memberPath = context.Arguments[0]?.ToString();
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);

        await foreach (var item in ShellIterationUtilities.ReplaySingleInputCollectionAsync(context.Input, context.CancellationToken)
                           .WithCancellation(context.CancellationToken))
        {
            var keyValue = memberPath is null ? item : context.Runtime.ObjectAccessor.GetValue(item, memberPath);
            var key = ShellDataSerializer.GetStableKey(keyValue);

            if (seen.Add(key))
            {
                yield return item;
            }
        }
    }
}
