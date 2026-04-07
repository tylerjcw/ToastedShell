namespace Tosh.Core.Commands;

[CommandCategory("Pipeline")]
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
