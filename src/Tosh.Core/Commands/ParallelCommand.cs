using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Tosh.Core.Commands;

[CommandCategory("Pipeline")]
[CommandArgument("callable|block", "A lambda or block executed once per input item, concurrently.")]
[CommandOption("--threads <n>", "Maximum degree of parallelism (default: processor count).")]
[CommandExample("echo 1 2 3 | parallel { echo (_ * 2) }", Title = "Double each item in parallel")]
[CommandOutput("Returns whatever values the callable or block emits for each input item, in input order.")]
[PipelineInput(AcceptsScalar = true, AcceptsRecord = true, Description = "Consumes the current pipeline and executes the callable or block in parallel for each input item.")]
public sealed class ParallelCommand : ShellCommand
{
    public ParallelCommand()
        : base("parallel", "Executes a block or callable concurrently for each input object.",
               "parallel [--threads <n>] <callable|block>")
    { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var (maxThreads, operationIndex) = ParseOptions(context);
        var operation = FunctionalCommandUtilities.RequireCallableOrBlock(context, operationIndex);
        var executor = context.Runtime.BlockExecutor;

        // Collect all input items so we can process them concurrently.
        var items = await AsyncEnumerableExtensions.ToListAsync(
            ShellIterationUtilities.ReplaySingleInputCollectionAsync(context.Input, context.CancellationToken),
            context.CancellationToken);

        // Engine block execution is single-threaded, so we serialize access.
        var engineLock = new SemaphoreSlim(1, 1);
        var results = new ConcurrentDictionary<int, List<object?>>();

        await Parallel.ForEachAsync(
            Enumerable.Range(0, items.Count),
            new ParallelOptions
            {
                MaxDegreeOfParallelism = maxThreads,
                CancellationToken = context.CancellationToken,
            },
            async (index, token) =>
            {
                var item = items[index];
                var values = new List<object?>();

                await engineLock.WaitAsync(token);
                try
                {
                    if (operation is ShellBlock block)
                    {
                        if (executor is null)
                        {
                            throw new InvalidOperationException("Block execution is not available in this runtime.");
                        }

                        await foreach (var value in executor.ExecuteAsync(
                            block,
                            new Dictionary<string, object?>(StringComparer.Ordinal) { ["_"] = item },
                            token))
                        {
                            values.Add(value);
                        }
                    }
                    else
                    {
                        var callResults = await FunctionalCommandUtilities.ExecuteAsync(
                            context,
                            operation,
                            [item],
                            new Dictionary<string, object?>(StringComparer.Ordinal) { ["_"] = item });

                        foreach (var value in callResults)
                        {
                            values.Add(value);
                        }
                    }
                }
                catch (BreakSignalException)
                {
                    // In parallel context, break stops the current item only.
                }
                catch (ContinueSignalException)
                {
                    // Continue is a no-op in parallel context.
                }
                finally
                {
                    engineLock.Release();
                }

                results[index] = values;
            });

        // Yield results in original input order.
        for (var i = 0; i < items.Count; i++)
        {
            if (results.TryGetValue(i, out var values))
            {
                foreach (var value in values)
                {
                    context.CancellationToken.ThrowIfCancellationRequested();
                    yield return value;
                }
            }
        }
    }

    private static (int MaxThreads, int OperationIndex) ParseOptions(CommandContext context)
    {
        var maxThreads = Environment.ProcessorCount;
        var operationIndex = 0;

        for (var i = 0; i < context.Arguments.Count; i++)
        {
            var text = context.Arguments[i]?.ToString();

            if (text is "--threads" or "-t")
            {
                maxThreads = CommandArguments.RequireConverted<int>(context.Arguments, ++i, "threads");
                operationIndex = i + 1;
                continue;
            }

            operationIndex = i;
            break;
        }

        if (operationIndex >= context.Arguments.Count)
        {
            throw context.CreateDiagnostic(
                code: "tosh::runtime::parallel_requires_callable_or_block",
                title: "'parallel' requires a callable value or block.",
                label: "pass a lambda like 'func(x) => ...' or a block like '{ ... }'");
        }

        return (maxThreads, operationIndex);
    }
}
