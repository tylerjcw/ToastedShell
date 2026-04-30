using System.Dynamic;

namespace Tosh.Core.Commands;

[Stdlib(StdlibCategory.Pipeline)]
[CommandCategory("Pipeline")]
[CommandArgument("member-path", "Optional member path to extract numeric values from each object.", Required = false)]
[CommandExample("echo 23 45 12 67 34 89 11 55 | describe", Title = "Summary statistics")]
[CommandOutput("A table of summary statistics: Count, Mean, Median, StdDev, Min, Max, Q1, Q3.")]
[PipelineInput(AcceptsScalar = true, AcceptsRecord = true, Description = "Consumes the pipeline and returns descriptive statistics.")]
public sealed class DescribeCommand : ShellCommand, ICurrentItemMemberPathCommand
{
    public DescribeCommand()
        : base("describe", "Returns descriptive statistics for numeric pipeline values.", "describe [member-path]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var values = await AggregationUtilities.CollectValuesAsync(context);

        if (values.Count == 0)
        {
            yield break;
        }

        if (!AggregationUtilities.TryGetDoubles(values, out var doubles))
        {
            throw new InvalidOperationException("describe expects numeric pipeline values.");
        }

        var sorted = doubles.OrderBy(v => v).ToArray();
        var count = sorted.Length;
        var mean = sorted.Average();
        var median = ComputeMedian(sorted);
        var variance = sorted.Select(v => (v - mean) * (v - mean)).Sum() / count;
        var stdev = Math.Sqrt(variance);
        var min = sorted[0];
        var max = sorted[^1];
        var q1 = ComputePercentile(sorted, 25.0);
        var q3 = ComputePercentile(sorted, 75.0);

        var stats = new (string Stat, object Value)[]
        {
            ("Count", count),
            ("Mean", RoundIfNeeded(mean)),
            ("Median", RoundIfNeeded(median)),
            ("StdDev", RoundIfNeeded(stdev)),
            ("Variance", RoundIfNeeded(variance)),
            ("Min", RoundIfNeeded(min)),
            ("Max", RoundIfNeeded(max)),
            ("Q1", RoundIfNeeded(q1)),
            ("Q3", RoundIfNeeded(q3)),
        };

        foreach (var (stat, value) in stats)
        {
            dynamic row = new ExpandoObject();
            ((IDictionary<string, object?>)row)["Stat"] = stat;
            ((IDictionary<string, object?>)row)["Value"] = value;
            yield return row;
        }
    }

    private static double ComputeMedian(double[] sorted)
    {
        int n = sorted.Length;
        if (n % 2 == 1) return sorted[n / 2];
        return (sorted[n / 2 - 1] + sorted[n / 2]) / 2.0;
    }

    private static double ComputePercentile(double[] sorted, double p)
    {
        if (sorted.Length == 1) return sorted[0];

        double rank = (p / 100.0) * (sorted.Length - 1);
        int lower = (int)Math.Floor(rank);
        int upper = (int)Math.Ceiling(rank);

        if (lower == upper) return sorted[lower];

        double fraction = rank - lower;
        return sorted[lower] + fraction * (sorted[upper] - sorted[lower]);
    }

    private static object RoundIfNeeded(double value)
    {
        if (value == Math.Floor(value) && !double.IsInfinity(value) && !double.IsNaN(value))
        {
            return (long)value;
        }

        return Math.Round(value, 6);
    }
}
