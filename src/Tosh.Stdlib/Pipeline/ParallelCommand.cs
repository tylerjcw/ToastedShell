using System.Collections.Concurrent;

using Tosh.Runtime;

namespace Tosh.Stdlib.Pipeline;

[Stdlib(StdlibCategory.Pipeline)]
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

        // Collect all input items so we can process them concurrently.
        var items = await AsyncEnumerableExtensions.ToListAsync(
            ShellIterationUtilities.ReplaySingleInputCollectionAsync(context.Input, context.CancellationToken),
            context.CancellationToken);

        // Fork the executor once per item so each invocation gets an isolated scope snapshot.
        // This replaces the old engineLock serialisation, enabling true concurrent execution.
        var baseExecutor = context.BlockExecutor ?? context.Runtime.BlockExecutor;
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
                var forkedContext = context with
                {
                    BlockExecutor = baseExecutor?.Fork(),
                    CancellationToken = token,
                };

                try
                {
                    var callResults = await FunctionalCommandUtilities.ExecuteAsync(
                        forkedContext,
                        operation,
                        [item],
                        new Dictionary<string, object?>(StringComparer.Ordinal) { ["_"] = item });

                    foreach (var value in callResults)
                    {
                        values.Add(value);
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
                code: "tosh.runtime.parallel_requires_callable_or_block",
                title: "'parallel' requires a callable value or block.",
                label: "pass a lambda like 'func(x) => ...' or a block like '{ ... }'");
        }

        return (maxThreads, operationIndex);
    }
}
