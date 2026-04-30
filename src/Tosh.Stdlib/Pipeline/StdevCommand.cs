using Tosh.Runtime;

namespace Tosh.Stdlib.Pipeline;

[CommandCategory("Pipeline")]
[CommandArgument("member-path", "Optional member path to extract numeric values from each object.", Required = false)]
[CommandExample("echo 2 4 4 4 5 5 7 9 | stdev", Title = "Standard deviation")]
[CommandOutput("The population standard deviation of the pipeline values.")]
[PipelineInput(AcceptsScalar = true, AcceptsRecord = true, Description = "Consumes the pipeline and returns the standard deviation.")]
public sealed class StdevCommand : ShellCommand, ICurrentItemMemberPathCommand
{
    public StdevCommand(string name = "stdev")
        : base(name, "Returns the population standard deviation of numeric pipeline values.", $"{name} [member-path]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var values = await AggregationUtilities.CollectValuesAsync(context);

        if (values.Count == 0)
        {
            yield break;
        }

        yield return AggregationUtilities.StandardDeviation(values);
    }
}
