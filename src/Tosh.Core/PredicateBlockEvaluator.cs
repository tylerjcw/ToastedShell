namespace Tosh.Core;

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
            if (!TypeConversion.TryConvert(output, typeof(bool), out var converted) || converted is not bool value)
            {
                throw context.CreateDiagnostic(
                    code: "tosh.runtime.predicate_requires_boolean",
                    title: "Predicate expressions must return boolean values.",
                    argumentIndex: 0,
                    label: "this predicate did not evaluate to true or false",
                    help: "return booleans, for example with 'Contains(...)', '==', '&&', or '!'.");
            }

            hasValue = true;

            if (!value)
            {
                return false;
            }
        }

        return hasValue;
    }
}
