namespace Tosh.Runtime;

internal static class PredicateBlockEvaluator
{
    public static async Task<bool> EvaluateAsync(
        CommandContext context,
        ShellBlock block,
        object? item)
    {
        var executor = context.Runtime.BlockExecutor
                       ?? throw new InvalidOperationException("Block execution is not available in this runtime.");

        var hasValue = false;

        await foreach (var output in executor.ExecuteAsync(
                           block,
                           new Dictionary<string, object?>(StringComparer.Ordinal)
                           {
                               ["_"] = item,
                           },
                           context.CancellationToken)
                           .WithCancellation(context.CancellationToken))
        {
            hasValue = true;

            if (!ToshTruthiness.IsTruthy(output))
            {
                return false;
            }
        }

        return hasValue;
    }
}
