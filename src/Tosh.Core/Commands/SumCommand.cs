namespace Tosh.Core.Commands;

public sealed class SumCommand : ShellCommand
{
    public SumCommand()
        : base("sum", "Sums numeric, storage size, or timespan values.", "sum [member-path]") { }

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
