namespace Tosh.Core.Commands;

public sealed class MaxCommand : ShellCommand
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
