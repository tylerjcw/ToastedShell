using Tosh.Runtime;

namespace Tosh.Stdlib.Pipeline;

[CommandCategory("Pipeline")]
[CommandArgument("member-path", "Optional member path to extract the comparison value from each object.", Required = false)]
[CommandExample("echo 3 1 4 1 5 | max", Title = "Find the maximum value")]
[CommandExample("ls | max .Length", Title = "Find the largest file by size")]
[CommandOutput("The maximum value from the pipeline.")]
[PipelineInput(AcceptsScalar = true, AcceptsRecord = true, Description = "Consumes the pipeline and returns the maximum value.")]
public sealed class MaxCommand : ShellCommand, ICurrentItemMemberPathCommand
{
    public MaxCommand()
        : base("max", "Returns the maximum pipeline value.", "max [member-path]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var values = await AggregationUtilities.CollectValuesAsync(context);

        if (values.Count == 0)
        {
            yield break;
        }

        yield return AggregationUtilities.Max(values);
    }
}
