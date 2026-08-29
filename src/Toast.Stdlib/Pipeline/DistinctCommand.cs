using Tosh.Runtime;

namespace Tosh.Stdlib.Pipeline;

[CommandCategory("Pipeline")]
[CommandArgument("member-path", "Optional member path to extract the comparison key from each object.", Required = false)]
[CommandExample("echo 1 2 2 3 1 | distinct", Title = "Remove duplicate values")]
[CommandExample("ls | distinct .Extension", Title = "Distinct by a member path")]
[CommandOutput("Pipeline items with duplicate values removed. Order is preserved.")]
[PipelineInput(AcceptsScalar = true, AcceptsRecord = true, Description = "Yields each pipeline object only the first time its value (or keyed value) is seen.")]
[CommandStreaming(StreamingBehavior.Lazy)]
public sealed class DistinctCommand : ShellCommand, ICurrentItemMemberPathCommand
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

        // `TOAST-0018`. Keyed by the shared key relation rather than by a JSON rendering
        // of the value. The rendering preserved field order, so `{| a = 1, b = 2 |}` and
        // `{| b = 2, a = 1 |}` — which `==` calls equal — both survived `distinct`.
        var seen = new HashSet<object?>(ShellKeyComparer.Instance);

        await foreach (var item in ShellIterationUtilities.ReplaySingleInputCollectionAsync(context.Input, context.CancellationToken)
                           .WithCancellation(context.CancellationToken))
        {
            var keyValue = memberPath is null ? item : context.LanguageRuntime.ObjectAccessor.GetValue(item, memberPath);
            if (seen.Add(keyValue))
            {
                yield return item;
            }
        }
    }
}
