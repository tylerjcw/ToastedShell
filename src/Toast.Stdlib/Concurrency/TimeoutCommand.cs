using Tosh.Runtime;

namespace Tosh.Stdlib.Concurrency;

[CommandCategory("Concurrency")]
[CommandArgument("seconds", "Timeout in seconds (supports fractional values).")]
[CommandArgument("operation", "A callable or block to execute.")]
[CommandArgument("args", "Optional callable arguments.", Required = false)]
[CommandExample("timeout 2 { sleep 1; echo ok }", Title = "Complete within timeout")]
[CommandExample("timeout 0.2 { sleep 1 }", Title = "Fail when elapsed")]
[CommandOutput("Replays values from the operation when it completes in time.")]
public sealed class TimeoutCommand : ShellCommand
{
    public TimeoutCommand()
        : base("timeout", "Runs an operation with a timeout.", "timeout <seconds> <callable|block> [args ...]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (context.Arguments.Count < 2)
        {
            throw context.CreateDiagnostic(
                code: "tosh.runtime.timeout_requires_seconds_and_operation",
                title: "'timeout' requires a duration and an operation.",
                label: "usage: timeout <seconds> <callable|block> [args ...]");
        }

        var seconds = CommandArguments.RequireConverted<double>(context.Arguments, 0, "seconds");
        if (seconds <= 0)
        {
            throw context.CreateDiagnostic(
                code: "tosh.runtime.timeout_invalid_duration",
                title: "'timeout' duration must be greater than zero.",
                argumentIndex: 0,
                label: "provide a positive number of seconds");
        }

        var operation = FunctionalCommandUtilities.RequireCallableOrBlock(context, 1);
        var callArguments = context.Arguments.Count > 2
            ? CommandArguments.Slice(context.Arguments, 2)
            : Array.Empty<object?>();

        var timeoutDuration = TimeSpan.FromSeconds(seconds);
        var baseExecutor = context.BlockExecutor ?? context.LanguageRuntime.BlockExecutor;
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);

        var operationContext = context with
        {
            BlockExecutor = baseExecutor?.Fork(),
            CancellationToken = linkedCts.Token,
        };

        var operationTask = FunctionalCommandUtilities.ExecuteAsync(
            operationContext,
            operation,
            callArguments,
            new Dictionary<string, object?>(StringComparer.Ordinal));

        var timeoutTask = Task.Delay(timeoutDuration, context.CancellationToken);
        var winner = await Task.WhenAny(operationTask, timeoutTask);

        if (winner == timeoutTask)
        {
            linkedCts.Cancel();
            throw context.CreateDiagnostic(
                code: "tosh.runtime.timeout_elapsed",
                title: $"Operation timed out after {timeoutDuration.TotalSeconds:0.###} seconds.",
                label: "increase the timeout or optimize the operation");
        }

        var outputs = await operationTask;
        foreach (var value in outputs)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            yield return value;
        }
    }
}
