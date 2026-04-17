namespace Tosh.Core.Commands;

[CommandCategory("Pipeline")]
[CommandArgument("member-path", "Optional member path to extract numeric values from each object.", Required = false)]
[CommandExample("echo 10 20 30 | average", Title = "Average a list of numbers")]
[CommandExample("ls | average .Length", Title = "Average file sizes")]
[CommandOutput("The arithmetic mean of the pipeline values. Supports numeric, StorageSize, and TimeSpan types.")]
[PipelineInput(AcceptsScalar = true, AcceptsRecord = true, Description = "Consumes the pipeline and returns the arithmetic mean.")]
public sealed class AverageCommand : ShellCommand, ICurrentItemMemberPathCommand
{
    public AverageCommand(string name = "average")
        : base(name, "Averages numeric, storage size, or timespan values.", $"{name} [member-path]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var values = await AggregationUtilities.CollectValuesAsync(context);

        if (values.Count == 0)
        {
            yield break;
        }

        yield return AggregationUtilities.Average(values);
    }
}
