namespace Tosh.Core.Commands.Concurrency;

[Stdlib(StdlibCategory.Concurrency)]
[CommandCategory("Concurrency")]
[CommandArgument("operation", "A callable or block to run in the background.")]
[CommandArgument("args", "Optional callable arguments.", Required = false)]
[CommandExample("var f = async { sleep 0.2; echo done }", Title = "Start a deferred operation")]
[CommandOutput("Returns a ShellFuture handle.")]
public sealed class AsyncCommand : ShellCommand
{
    public AsyncCommand()
        : base("async", "Starts a callable or block and returns a future handle.", "async <callable|block> [args ...]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        await Task.CompletedTask;

        var operation = FunctionalCommandUtilities.RequireCallableOrBlock(context, 0);
        var callArguments = context.Arguments.Count > 1
            ? CommandArguments.Slice(context.Arguments, 1)
            : Array.Empty<object?>();

        var baseExecutor = context.BlockExecutor ?? context.Runtime.BlockExecutor;
        var futureContext = context with
        {
            BlockExecutor = baseExecutor?.Fork(),
            CancellationToken = CancellationToken.None,
        };

        var task = Task.Run(
            () => FunctionalCommandUtilities.ExecuteAsync(
                futureContext,
                operation,
                callArguments,
                new Dictionary<string, object?>(StringComparer.Ordinal)),
            CancellationToken.None);

        yield return new ShellFuture(task);
    }
}
