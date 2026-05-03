using Tosh.Runtime;

namespace Tosh.Stdlib.Pipeline;

[CommandCategory("Pipeline")]
[CommandArgument("[column|member-path] [--sum [columns]] [--avg [columns]] [--min [columns]] [--max [columns]] [--count [columns]]", "With no arguments, infer every sensible aggregate for every summarizable column. A single bare column or member path such as `Size` or `_.Used` narrows auto mode to that one target. Flags request explicit operations.", Required = false)]
[CommandOption("--sum [columns]", "Compute sums for scalar input or the named columns.")]
[CommandOption("--avg [columns], --average [columns]", "Compute averages for scalar input or the named columns.")]
[CommandOption("--min [columns]", "Compute minima for scalar input or the named columns.")]
[CommandOption("--max [columns]", "Compute maxima for scalar input or the named columns.")]
[CommandOption("--count [columns]", "Count input rows when no columns are supplied, or non-null values for the named columns.")]
[CommandExample("df | summarize", Title = "Infer every sensible aggregate for every summarizable df column")]
[CommandExample("df | summarize _.Used", Title = "Infer every sensible aggregate for a single member path target")]
[CommandExample("seq 5 | summarize --sum --avg --min --max --count", Title = "Summarize a scalar numeric pipeline explicitly")]
[CommandExample("ps | summarize --avg Memory --max Memory", Title = "Compute multiple aggregates over one column")]
[CommandOutput("Returns ColumnSummary objects describing the requested or inferred aggregates. The original input rows are not appended back into the result.", ClrType = typeof(IAsyncEnumerable<ColumnSummary>))]
[PipelineInput(AcceptsScalar = true, AcceptsRecord = true, AcceptsList = true, AcceptsTable = true, Description = "Consumes the current pipeline rows and returns one structured ColumnSummary object per requested or inferred scalar target or member path.")]
public sealed class SummarizeCommand : ShellCommand, ICurrentItemMemberPathCommand
{
    public SummarizeCommand(string name = "summarize")
        : base(name, "Computes structured summary objects for requested aggregates over the pipeline.", $"{name} [--sum [columns]] [--avg [columns]] [--min [columns]] [--max [columns]] [--count [columns]]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        IReadOnlyList<object?> results;

        var autoColumn = TryParseAutoMode(context.Arguments, out var singleColumn) ? singleColumn : null;

        if (autoColumn is not null || context.Arguments.Count == 0)
        {
            results = await AggregationUtilities.SummarizeAutoAsync(context, autoColumn);
        }
        else
        {
            var request = ParseExplicitRequest(context.Arguments);
            results = await AggregationUtilities.SummarizeAsync(context, request);
        }

        foreach (var result in results)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            yield return result;
        }
    }

    // Returns true (with singleColumn = null) for bare no-args, or true (with singleColumn set)
    // for a single bare column name like `summarize Size`. Returns false if flags are present.
    private static bool TryParseAutoMode(IReadOnlyList<object?> arguments, out string? singleColumn)
    {
        if (arguments.Count == 0)
        {
            singleColumn = null;
            return true;
        }

        if (arguments.Count == 1)
        {
            var arg = ExternalTextSerializer.SerializeArgument(arguments[0]);

            if (!arg.StartsWith("--", StringComparison.Ordinal) && !arg.StartsWith("-", StringComparison.Ordinal))
            {
                singleColumn = arg;
                return true;
            }
        }

        singleColumn = null;
        return false;
    }

    private static IReadOnlyList<AggregationUtilities.SummaryTarget> ParseExplicitRequest(IReadOnlyList<object?> arguments)
    {
        var serialized = arguments
            .Select(ExternalTextSerializer.SerializeArgument)
            .ToArray();

        var builder = new SummaryAggregationRequestBuilder();

        for (var index = 0; index < serialized.Length; index++)
        {
            var argument = serialized[index];

            switch (argument)
            {
                case "--sum":
                    builder.Add(SummaryOperation.Sum, TryConsumeColumns(serialized, ref index));
                    break;
                case "--avg":
                case "--average":
                    builder.Add(SummaryOperation.Average, TryConsumeColumns(serialized, ref index));
                    break;
                case "--min":
                    builder.Add(SummaryOperation.Min, TryConsumeColumns(serialized, ref index));
                    break;
                case "--max":
                    builder.Add(SummaryOperation.Max, TryConsumeColumns(serialized, ref index));
                    break;
                case "--count":
                    builder.Add(SummaryOperation.Count, TryConsumeColumns(serialized, ref index));
                    break;
                default:
                    throw new InvalidOperationException($"Unknown summarize option '{argument}'. Use flags like '--sum', '--avg', '--min', '--max', '--count', or pass a column name for auto mode.");
            }
        }

        return builder.Build();
    }

    private static IReadOnlyList<string> TryConsumeColumns(IReadOnlyList<string> arguments, ref int index)
    {
        if (index + 1 >= arguments.Count)
        {
            return Array.Empty<string>();
        }

        var next = arguments[index + 1];

        if (string.IsNullOrWhiteSpace(next) ||
            next.StartsWith("-", StringComparison.Ordinal))
        {
            return Array.Empty<string>();
        }

        index++;

        return next
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(column => !string.IsNullOrWhiteSpace(column))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private enum SummaryOperation
    {
        Sum,
        Average,
        Min,
        Max,
        Count,
    }

    private sealed class SummaryAggregationRequestBuilder
    {
        private readonly List<AggregationUtilities.SummaryTarget> _targets = [];

        public void Add(SummaryOperation operation, IReadOnlyList<string> columns)
        {
            if (columns.Count == 0)
            {
                AddOperation(ScalarColumnName, operation);
                return;
            }

            foreach (var column in columns)
            {
                AddOperation(column, operation);
            }
        }

        public IReadOnlyList<AggregationUtilities.SummaryTarget> Build()
        {
            if (_targets.Count == 0)
            {
                throw new InvalidOperationException("summarize requires at least one aggregate option such as '--sum Size' or '--count'.");
            }

            return _targets;
        }

        private void AddOperation(string column, SummaryOperation operation)
        {
            var normalizedPath = AggregationUtilities.NormalizeMemberPath(column);
            var columnName = string.IsNullOrWhiteSpace(normalizedPath) ||
                             string.Equals(normalizedPath, ScalarColumnName, StringComparison.OrdinalIgnoreCase)
                ? ScalarColumnName
                : normalizedPath;

            var existing = _targets.FirstOrDefault(target => string.Equals(target.Column, columnName, StringComparison.OrdinalIgnoreCase));
            var mappedOperation = MapOperation(operation);

            if (existing is not null)
            {
                existing.Operations.Add(mappedOperation);
                return;
            }

            var target = new AggregationUtilities.SummaryTarget(
                columnName,
                string.Equals(columnName, ScalarColumnName, StringComparison.OrdinalIgnoreCase) ? null : normalizedPath,
                []);
            target.Operations.Add(mappedOperation);
            _targets.Add(target);
        }
    }

    private static AggregationUtilities.SummaryOperationKind MapOperation(SummaryOperation operation)
    {
        return operation switch
        {
            SummaryOperation.Sum => AggregationUtilities.SummaryOperationKind.Sum,
            SummaryOperation.Average => AggregationUtilities.SummaryOperationKind.Average,
            SummaryOperation.Min => AggregationUtilities.SummaryOperationKind.Min,
            SummaryOperation.Max => AggregationUtilities.SummaryOperationKind.Max,
            SummaryOperation.Count => AggregationUtilities.SummaryOperationKind.Count,
            _ => throw new InvalidOperationException($"Unsupported summary operation '{operation}'."),
        };
    }

    private const string ScalarColumnName = "Value";
}
