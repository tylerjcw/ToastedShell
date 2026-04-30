using Tosh.Runtime;

namespace Tosh.Stdlib.Pipeline;

[CommandCategory("Pipeline")]
[CommandArgument("member-path", "Optional member path to extract numeric values from each object.", Required = false)]
[CommandExample("echo 1 2 3 4 5 | median", Title = "Median of a list")]
[CommandExample("ls | median .Length", Title = "Median file size")]
[CommandOutput("The median value of the pipeline. For even-length lists, returns the average of the two middle values.")]
[PipelineInput(AcceptsScalar = true, AcceptsRecord = true, Description = "Consumes the pipeline and returns the median.")]
public sealed class MedianCommand : ShellCommand, ICurrentItemMemberPathCommand
{
    public MedianCommand()
        : base("median", "Returns the median of numeric pipeline values.", "median [member-path]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var values = await AggregationUtilities.CollectValuesAsync(context);

        if (values.Count == 0)
        {
            yield break;
        }

        yield return AggregationUtilities.Median(values);
    }
}
