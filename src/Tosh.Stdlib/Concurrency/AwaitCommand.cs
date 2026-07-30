using Tosh.Runtime;

namespace Tosh.Stdlib.Concurrency;

[CommandCategory("Concurrency")]
[CommandArgument("awaitable", "A future from `async`, or a CLR Task/ValueTask. If omitted, read one from pipeline input.", Required = false)]
[CommandExample("var f = async { sleep 0.1; echo ok }; await $f", Title = "Await a future")]
[CommandExample("var r = await ($p.SendPingAsync(\"8.8.8.8\", 1000)); echo $r.Status", Title = "Await a CLR task")]
[CommandOutput("Replays the values produced by the operation, or the task's result.")]
public sealed class AwaitCommand : ShellCommand
{
    public AwaitCommand()
        : base("await", "Awaits a future from `async` or a CLR Task, and replays its outputs.", "await [awaitable]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var source = await ResolveAwaitableAsync(context);

        if (source is ShellFuture future)
        {
            var values = await AwaitFutureAsync(context, future);

            foreach (var value in values)
            {
                context.CancellationToken.ThrowIfCancellationRequested();

                // Flattened: a block that ends in a CLR async call produces a future
                // whose output is a task, and one `await` unwraps both. That is a
                // deliberate departure from C#, where this needs Task.Unwrap — a
                // future-of-task has no use here, and leaving it unflattened is what
                // made `async { $p.SendPingAsync(…) }` return a state machine box.
                if (ClrAwaitable.IsAwaitable(value))
                {
                    var (inner, hasInner) = await AwaitClrAsync(context, value!);

                    if (hasInner)
                    {
                        yield return inner;
                    }

                    continue;
                }

                yield return value;
            }

            yield break;
        }

        var (result, hasResult) = await AwaitClrAsync(context, source);

        if (hasResult)
        {
            yield return result;
        }
    }

    /// <summary>
    /// Awaits a CLR awaitable, converting a failure into a ToastScript diagnostic.
    /// </summary>
    /// <remarks>
    /// A faulted task throws <see cref="AggregateException"/>, whose message is
    /// "One or more errors occurred" — useless in a `catch (e)` block, which is where
    /// this lands in practice. One layer is unwrapped so <c>$e.Message</c> is the
    /// message the operation actually failed with.
    /// </remarks>
    private static async Task<(object? Result, bool HasResult)> AwaitClrAsync(
        CommandContext context,
        object awaitable)
    {
        try
        {
            return await ClrAwaitable.AwaitAsync(awaitable, context.CancellationToken);
        }
        catch (AggregateException aggregate) when (aggregate.InnerExceptions.Count == 1)
        {
            throw aggregate.InnerExceptions[0];
        }
    }

    private static async Task<IReadOnlyList<object?>> AwaitFutureAsync(
        CommandContext context,
        ShellFuture future)
    {
        try
        {
            return await future.AwaitAsync(context.CancellationToken);
        }
        catch (AggregateException aggregate) when (aggregate.InnerExceptions.Count == 1)
        {
            throw aggregate.InnerExceptions[0];
        }
    }

    /// <summary>
    /// Resolves what to await: an explicit argument, else the first piped value.
    /// </summary>
    private static async Task<object> ResolveAwaitableAsync(CommandContext context)
    {
        if (context.Arguments.Count > 0)
        {
            if (context.Arguments[0] is ShellFuture argFuture)
            {
                return argFuture;
            }

            if (ClrAwaitable.IsAwaitable(context.Arguments[0]))
            {
                return context.Arguments[0]!;
            }

            throw context.CreateDiagnostic(
                code: "tosh.runtime.await_requires_future",
                title: "'await' expects a future or a CLR Task.",
                argumentIndex: 0,
                label: "this value is not awaitable",
                help: "pass a future from 'async', or a value returned by a CLR async method.");
        }

        await foreach (var item in context.Input.WithCancellation(context.CancellationToken))
        {
            if (item is ShellFuture pipedFuture)
            {
                return pipedFuture;
            }

            if (ClrAwaitable.IsAwaitable(item))
            {
                return item!;
            }

            throw context.CreateDiagnostic(
                code: "tosh.runtime.await_requires_future",
                title: "'await' expects a future or a CLR Task.",
                label: "piped value is not awaitable",
                help: "pass a future from 'async', or a value returned by a CLR async method.");
        }

        throw context.CreateDiagnostic(
            code: "tosh.runtime.await_requires_future",
            title: "'await' requires an awaitable argument or piped input.",
            label: "pass a future from 'async', or a CLR Task");
    }
}
