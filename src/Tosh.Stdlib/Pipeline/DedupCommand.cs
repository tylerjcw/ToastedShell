using Tosh.Runtime;

namespace Tosh.Stdlib.Pipeline;

[CommandCategory("Pipeline")]
[CommandArgument("member-path", "Optional member path to extract the comparison key from each object.", Required = false)]
[CommandExample("echo 1 1 2 2 3 1 1 | dedup", Title = "Remove consecutive duplicates")]
[CommandExample("ls | sort-by .Extension | dedup .Extension", Title = "Dedup by member path")]
[CommandNote("Unlike `distinct`, only removes adjacent duplicates. Preserves non-consecutive repeats.")]
[CommandOutput("Pipeline items with consecutive duplicate values removed.")]
[PipelineInput(AcceptsScalar = true, AcceptsRecord = true, Description = "Yields each item only if different from the previous.")]
public sealed class DedupCommand : ShellCommand, ICurrentItemMemberPathCommand
{
    public DedupCommand()
        : base("dedup", "Removes consecutive duplicate values from the pipeline. Unlike 'distinct', preserves non-adjacent duplicates.", "... | dedup [member-path]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (context.Arguments.Count > 1)
        {
            throw context.CreateDiagnostic(
                code: "tosh.runtime.dedup_too_many_args",
                title: "'dedup' accepts at most one member path argument.",
                label: "use '... | dedup [member-path]'");
        }

        string? memberPath = context.Arguments.Count == 1 ? context.Arguments[0]?.ToString() : null;

        string? lastKey = null;
        var first = true;

        await foreach (var item in ShellIterationUtilities.ReplaySingleInputCollectionAsync(context.Input, context.CancellationToken)
                           .WithCancellation(context.CancellationToken))
        {
            var keyValue = memberPath is null ? item : context.Runtime.ObjectAccessor.GetValue(item, memberPath);
            var key = ShellDataSerializer.GetStableKey(keyValue);

            if (first || !string.Equals(key, lastKey, StringComparison.Ordinal))
            {
                yield return item;
                lastKey = key;
                first = false;
            }
        }
    }
}
