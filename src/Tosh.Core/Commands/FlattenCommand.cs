using System.Collections;

namespace Tosh.Core.Commands;

[CommandCategory("Pipeline")]
public sealed class FlattenCommand : ShellCommand
{
    public FlattenCommand()
        : base("flatten", "Explicitly expands enumerable pipeline values by one level.", "flatten") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (context.Arguments.Count > 0)
        {
            throw new InvalidOperationException("flatten does not accept arguments.");
        }

        await foreach (var item in context.Input.WithCancellation(context.CancellationToken))
        {
            if (item is null ||
                item is string ||
                item is ShellTextLine ||
                item is IDictionary ||
                ShellRecordUtilities.IsRecordLike(item) ||
                item is not IEnumerable enumerable)
            {
                yield return item;
                continue;
            }

            foreach (var child in enumerable)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                yield return child;
            }
        }
    }
}
