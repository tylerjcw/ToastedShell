namespace Tosh.Core.Commands;

[CommandCategory("Pipeline")]
[CommandArgument("member-path", "Optional member path to extract the comparison value from each object.", Required = false)]
[CommandExample("echo 3 1 4 1 5 | min", Title = "Find the minimum value")]
[CommandExample("ls | min .Length", Title = "Find the smallest file by size")]
[CommandOutput("The minimum value from the pipeline.")]
[PipelineInput(AcceptsScalar = true, AcceptsRecord = true, Description = "Consumes the pipeline and returns the minimum value.")]
public sealed class MinCommand : ShellCommand, ICurrentItemMemberPathCommand
{
    public MinCommand()
        : base("min", "Returns the minimum pipeline value.", "min [member-path]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var values = await AggregationUtilities.CollectValuesAsync(context);

        if (values.Count == 0)
        {
            yield break;
        }

        yield return AggregationUtilities.Min(values);
    }
}
