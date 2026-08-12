using Tosh.Runtime;

namespace Tosh.Stdlib.Pipeline;

[CommandCategory("Pipeline")]
[CommandArgument("member-path", "Optional member path to extract numeric values from each object.", Required = false)]
[CommandExample("echo 1 2 3 4 | sum", Title = "Sum a list of numbers")]
[CommandExample("echo 1`km 500`m | sum", Title = "Sum compatible quantities")]
[CommandExample("ls | sum .Length", Title = "Total file sizes")]
[CommandOutput("The sum of the pipeline values. Supports numeric, Quantity, StorageSize, and TimeSpan types.")]
[PipelineInput(AcceptsScalar = true, AcceptsRecord = true, Description = "Consumes the pipeline and returns the total.")]
[CommandStreaming(StreamingBehavior.Eager)]
public sealed class SumCommand : ShellCommand, ICurrentItemMemberPathCommand
{
    public SumCommand()
        : base("sum", "Sums numeric, quantity, storage size, or timespan values.", "sum [member-path]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var values = await AggregationUtilities.CollectValuesAsync(context);

        if (values.Count == 0)
        {
            yield break;
        }

        yield return AggregationUtilities.Sum(values);
    }
}
