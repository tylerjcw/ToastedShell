namespace Tosh.Core.Commands.Pipeline;

[Stdlib(StdlibCategory.Pipeline)]
[CommandCategory("Pipeline")]
[CommandArgument("member-path", "Optional member path to extract numeric values from each object.", Required = false)]
[CommandExample("echo 2 4 4 4 5 5 7 9 | variance", Title = "Population variance")]
[CommandOutput("The population variance of the pipeline values.")]
[PipelineInput(AcceptsScalar = true, AcceptsRecord = true, Description = "Consumes the pipeline and returns the variance.")]
public sealed class VarianceCommand : ShellCommand, ICurrentItemMemberPathCommand
{
    public VarianceCommand()
        : base("variance", "Returns the population variance of numeric pipeline values.", "variance [member-path]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var values = await AggregationUtilities.CollectValuesAsync(context);

        if (values.Count == 0)
        {
            yield break;
        }

        yield return AggregationUtilities.Variance(values);
    }
}
