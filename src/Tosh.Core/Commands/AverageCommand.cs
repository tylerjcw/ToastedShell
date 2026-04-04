namespace Tosh.Core.Commands;

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
