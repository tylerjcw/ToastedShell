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

        var outputs = new List<object?>();

        await foreach (var value in executor.ExecuteAsync(
                           block,
                           new Dictionary<string, object?>(StringComparer.Ordinal)
                           {
                               ["it"] = item,
                           },
                           context.CancellationToken)
                           .WithCancellation(context.CancellationToken))
        {
            outputs.Add(value);
        }

        if (outputs.Count == 0)
        {
            return false;
        }

        if (outputs.Count != 1)
        {
            throw context.CreateDiagnostic(
                code: "tosh::runtime::predicate_requires_single_value",
                title: "Predicate expressions must produce exactly one value for each input object.",
                argumentIndex: 0,
                label: $"this predicate produced {outputs.Count} values for one object",
                help: "return a single boolean value from the predicate.");
        }

        if (!TypeConversion.TryConvert(outputs[0], typeof(bool), out var converted) || converted is not bool matches)
        {
            throw context.CreateDiagnostic(
                code: "tosh::runtime::predicate_requires_boolean",
                title: "Predicate expressions must return a boolean value.",
                argumentIndex: 0,
                label: "this predicate did not evaluate to true or false",
                help: "return a boolean, for example with 'Contains(...)' or '=='.");
        }

        return matches;
    }
}
