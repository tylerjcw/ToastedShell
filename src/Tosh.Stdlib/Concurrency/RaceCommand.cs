using Tosh.Runtime;

namespace Tosh.Stdlib.Concurrency;

[CommandCategory("Concurrency")]
[CommandArgument("operation", "A callable or block to execute. Provide two or more operations to race.", Variadic = true)]
[CommandExample("race { sleep 0.1; echo slow } { sleep 0.01; echo fast }", Title = "Return the first completed operation")]
[CommandOutput("Returns the first completed operation result.")]
public sealed class RaceCommand : ShellCommand
{
    public RaceCommand()
        : base("race", "Executes operations concurrently and returns the first completion.", "race <callable|block> <callable|block> [...]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (context.Arguments.Count < 2)
        {
            throw context.CreateDiagnostic(
                code: "tosh.runtime.race_requires_multiple_operations",
                title: "'race' requires at least two callable values or blocks.",
                label: "pass two or more lambdas or blocks");
        }

        var operations = context.Arguments
            .Select((_, index) => FunctionalCommandUtilities.RequireCallableOrBlock(context, index))
            .ToArray();

        // Fork the executor once per operation at the time race is called so each
        // arm gets an isolated scope snapshot.  This enables true concurrent execution
        // with no serialisation lock.
        var baseExecutor = context.BlockExecutor ?? context.LanguageRuntime.BlockExecutor;

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);

        var pending = operations
            .Select((operation, index) =>
            {
                var forkedContext = context with
                {
                    BlockExecutor = baseExecutor?.Fork(),
                    CancellationToken = linkedCts.Token,
                };
                return ExecuteOperationAsync(forkedContext, operation, index);
            })
            .ToList();

        var winnerTask = await Task.WhenAny(pending);
        var winner = await winnerTask;

        linkedCts.Cancel();

        try
        {
            await Task.WhenAll(pending);
        }
        catch (OperationCanceledException)
        {
            // Expected for non-winning operations after cancellation.
        }
        catch
        {
            // Ignore non-winning failures because race only surfaces the first completion.
        }

        if (!winner.Succeeded)
        {
            throw winner.Exception ?? new InvalidOperationException("Race operation failed.");
        }

        yield return CollapseOutputs(winner.Outputs);
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
