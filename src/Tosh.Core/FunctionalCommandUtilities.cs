namespace Tosh.Core;

internal static class FunctionalCommandUtilities
{
    public static object RequireCallableOrBlock(CommandContext context, int argumentIndex)
    {
        if (argumentIndex >= context.Arguments.Count)
        {
            throw context.CreateDiagnostic(
                code: CreateDiagnosticCode(context, "requires_callable_or_block"),
                title: $"'{GetCommandName(context)}' requires a callable value or block.",
                argumentIndex: argumentIndex,
                label: "pass a lambda like 'func(x) => ...' or a block like '{ ... }'");
        }

        var operation = context.Arguments[argumentIndex];

        if (operation is IShellCallable or ShellBlock)
        {
            return operation;
        }

        throw context.CreateDiagnostic(
            code: CreateDiagnosticCode(context, "requires_callable_or_block"),
            title: $"'{GetCommandName(context)}' requires a callable value or block.",
            argumentIndex: argumentIndex,
            label: "this value is not callable",
            help: "pass a lambda like 'func(x) => ...' or a block like '{ ... }'.");
    }

    public static async Task<object> ResolveCallableOrBlockAsync(CommandContext context, object operation)
    {
        if (operation is not ShellBlock block)
        {
            return operation;
        }

        try
        {
            var results = await ExecuteAsync(
                context,
                block,
                Array.Empty<object?>(),
                new Dictionary<string, object?>(StringComparer.Ordinal));

            if (results.Count == 1 && results[0] is IShellCallable callable)
            {
                return callable;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Leave normal predicate blocks untouched. If the block still
            // fails when evaluated against real input, that diagnostic will
            // surface in the usual execution path.
        }

        return block;
    }

    public static async Task<IReadOnlyList<object?>> ExecuteAsync(
        CommandContext context,
        object operation,
        IReadOnlyList<object?> callableArguments,
        IReadOnlyDictionary<string, object?>? blockLocals = null)
    {
        switch (operation)
        {
            case IShellCallable callable:
            {
                var invokeContext = context with
                {
                    Arguments = callableArguments,
                    Input = AsyncEnumerableExtensions.Empty<object?>(),
                    IsPipelined = false,
                };

                return await AsyncEnumerableExtensions.ToListAsync(
                    callable.InvokeAsync(invokeContext),
                    context.CancellationToken);
            }

            case ShellBlock block:
            {
                var executor = context.Runtime.BlockExecutor
                               ?? throw new InvalidOperationException("Block execution is not available in this runtime.");

                var locals = new Dictionary<string, object?>(StringComparer.Ordinal);

                if (blockLocals is not null)
                {
                    foreach (var (name, value) in blockLocals)
                    {
                        locals[name] = value;
                    }
                }

                return await AsyncEnumerableExtensions.ToListAsync(
                    executor.ExecuteAsync(block, locals, context.CancellationToken),
                    context.CancellationToken);
            }

            default:
                throw context.CreateDiagnostic(
                    code: CreateDiagnosticCode(context, "requires_callable_or_block"),
                    title: $"'{GetCommandName(context)}' requires a callable value or block.",
                    label: "this value is not callable");
        }
    }

    public static async Task<object?> RequireSingleResultAsync(
        CommandContext context,
        object operation,
        IReadOnlyList<object?> callableArguments,
        IReadOnlyDictionary<string, object?>? blockLocals = null)
    {
        var results = await ExecuteAsync(context, operation, callableArguments, blockLocals);

        if (results.Count == 1)
        {
            return results[0];
        }

        throw context.CreateDiagnostic(
            code: CreateDiagnosticCode(context, "requires_single_result"),
            title: $"'{GetCommandName(context)}' operations must produce exactly one value per input item.",
            label: results.Count == 0
                ? "this operation produced no values"
                : $"this operation produced {results.Count} values",
            help: "return exactly one value from the lambda or block for each input item.");
    }

    public static async Task<bool> EvaluatePredicateAsync(
        CommandContext context,
        object operation,
        IReadOnlyList<object?> callableArguments,
        IReadOnlyDictionary<string, object?>? blockLocals = null)
    {
        var results = await ExecuteAsync(context, operation, callableArguments, blockLocals);
        var hasValue = false;

        foreach (var output in results)
        {
            if (!TypeConversion.TryConvert(output, typeof(bool), out var converted) || converted is not bool value)
            {
                throw context.CreateDiagnostic(
                    code: CreateDiagnosticCode(context, "predicate_requires_boolean"),
                    title: $"'{GetCommandName(context)}' predicates must return boolean values.",
                    label: "this predicate did not evaluate to true or false",
                    help: "return booleans, for example with '==', 'Contains(...)', or another predicate.");
            }

            hasValue = true;

            if (!value)
            {
                return false;
            }
        }

        return hasValue;
    }

    private static string GetCommandName(CommandContext context)
        => context.Invocation?.CommandName ?? "command";

    private static string CreateDiagnosticCode(CommandContext context, string suffix)
        => $"tosh::runtime::{GetCommandName(context).Replace("-", "_", StringComparison.Ordinal)}_{suffix}";
}
