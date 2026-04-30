using Tosh.Runtime;

namespace Tosh.Stdlib.Concurrency;

[CommandCategory("Concurrency")]
[CommandArgument("future", "A ShellFuture value. If omitted, read one from pipeline input.", Required = false)]
[CommandExample("var f = async { sleep 0.1; echo ok }; await $f", Title = "Await a future")]
[CommandOutput("Replays the values produced by the future operation.")]
public sealed class AwaitCommand : ShellCommand
{
    public AwaitCommand()
        : base("await", "Awaits a future created by async and replays its outputs.", "await [future]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var future = await ResolveFutureAsync(context);
        var values = await future.AwaitAsync(context.CancellationToken);

        foreach (var value in values)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            yield return value;
        }
    }

    private static async Task<ShellFuture> ResolveFutureAsync(CommandContext context)
    {
        if (context.Arguments.Count > 0)
        {
            if (context.Arguments[0] is ShellFuture argFuture)
            {
                return argFuture;
            }

            throw context.CreateDiagnostic(
                code: "tosh.runtime.await_requires_future",
                title: "'await' expects a ShellFuture value.",
                argumentIndex: 0,
                label: "this value is not a future");
        }

        await foreach (var item in context.Input.WithCancellation(context.CancellationToken))
        {
            if (item is ShellFuture pipedFuture)
            {
                return pipedFuture;
            }

            throw context.CreateDiagnostic(
                code: "tosh.runtime.await_requires_future",
                title: "'await' expects a ShellFuture value.",
                label: "piped value is not a future");
        }

        throw context.CreateDiagnostic(
            code: "tosh.runtime.await_requires_future",
            title: "'await' requires a ShellFuture argument or piped input.",
            label: "pass a future created by 'async'");
    }
}
