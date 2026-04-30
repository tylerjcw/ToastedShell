using Tosh.Runtime;

namespace Tosh.Stdlib.Pipeline;

[CommandCategory("Pipeline")]
[CommandArgument("percentile", "The percentile to compute (0–100).", Required = true)]
[CommandArgument("member-path", "Optional member path to extract numeric values from each object.", Required = false)]
[CommandExample("echo 1 2 3 4 5 6 7 8 9 10 | percentile 95", Title = "95th percentile")]
[CommandOutput("The value at the requested percentile using linear interpolation.")]
[PipelineInput(AcceptsScalar = true, AcceptsRecord = true, Description = "Consumes the pipeline and returns the percentile value.")]
public sealed class PercentileCommand : ShellCommand, ICurrentItemMemberPathCommand
{
    public PercentileCommand()
        : base("percentile", "Returns the Nth percentile of numeric pipeline values.", "percentile <N> [member-path]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (context.Arguments.Count < 1)
        {
            throw new InvalidOperationException("percentile requires a percentile value (0–100) as the first argument.");
        }

        if (!double.TryParse(context.Arguments[0]?.ToString(), out var p) || p < 0 || p > 100)
        {
            throw new InvalidOperationException($"Invalid percentile value '{context.Arguments[0]}'. Must be a number between 0 and 100.");
        }

        // Shift arguments so CollectValuesAsync sees the member-path (if any) as argument[0].
        var innerContext = context.Arguments.Count > 1
            ? context with { Arguments = context.Arguments.Skip(1).ToList() }
            : context with { Arguments = Array.Empty<object?>() };

        var values = await AggregationUtilities.CollectValuesAsync(innerContext);

        if (values.Count == 0)
        {
            yield break;
        }

        yield return AggregationUtilities.Percentile(values, p);
    }
}
