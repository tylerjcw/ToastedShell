namespace Tosh.Core.Commands.Concurrency;

[Stdlib(StdlibCategory.Concurrency)]
[CommandCategory("Concurrency")]
[CommandArgument("operation", "A callable or block to execute. Provide one or more operations to settle.")]
[CommandExample("settle { echo ok } { throw boom }", Title = "Collect fulfilled and rejected outcomes")]
[CommandOutput("Returns one outcome object per operation with Status, Value, and Error fields.")]
public sealed class SettleCommand : ShellCommand
{
    public SettleCommand()
        : base("settle", "Executes operations concurrently and returns settled outcomes.", "settle <callable|block> [...]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (context.Arguments.Count < 1)
        {
            throw context.CreateDiagnostic(
                code: "tosh.runtime.settle_requires_operation",
                title: "'settle' requires at least one callable value or block.",
                label: "pass one or more lambdas or blocks");
        }

        var operations = context.Arguments
            .Select((_, index) => FunctionalCommandUtilities.RequireCallableOrBlock(context, index))
            .ToArray();

        // Fork the executor once per operation so each arm gets an isolated scope snapshot,
        // enabling true concurrent execution with no serialisation lock.
        var baseExecutor = context.BlockExecutor ?? context.Runtime.BlockExecutor;

        var tasks = operations
            .Select((operation, index) =>
            {
                var forkedContext = context with { BlockExecutor = baseExecutor?.Fork() };
                return ExecuteOperationAsync(forkedContext, operation, index);
            })
            .ToArray();

        var outcomes = await Task.WhenAll(tasks);

        foreach (var outcome in outcomes.OrderBy(item => item.Index))
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            yield return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["Index"] = outcome.Index,
                ["Status"] = outcome.Succeeded ? "fulfilled" : "rejected",
                ["Value"] = outcome.Succeeded ? CollapseOutputs(outcome.Outputs) : null,
                ["Error"] = outcome.Succeeded ? null : outcome.Exception?.Message,
            };
        }
    }

    private static async Task<OperationResult> ExecuteOperationAsync(
        CommandContext context,
        object operation,
        int index)
    {
        try
        {
            var outputs = await FunctionalCommandUtilities.ExecuteAsync(
                context,
                operation,
                Array.Empty<object?>(),
                new Dictionary<string, object?>(StringComparer.Ordinal));

            return new OperationResult(index, true, outputs, null);
        }
        catch (Exception ex)
        {
            return new OperationResult(index, false, Array.Empty<object?>(), ex);
        }
    }

    private static object? CollapseOutputs(IReadOnlyList<object?> outputs)
    {
        return outputs.Count switch
        {
            0 => null,
            1 => outputs[0],
            _ => outputs.ToArray(),
        };
    }

    private sealed record OperationResult(
        int Index,
        bool Succeeded,
        IReadOnlyList<object?> Outputs,
        Exception? Exception);
}
