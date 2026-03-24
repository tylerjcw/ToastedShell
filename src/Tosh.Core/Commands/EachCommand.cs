using System.Collections;

namespace Tosh.Core.Commands;

public sealed class EachCommand : ShellCommand
{
    public EachCommand()
        : base("each", "Executes a block once for each input object.", "each { <statement>; ... }") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (context.Arguments.Count != 1 || context.Arguments[0] is not ShellBlock block)
        {
            throw new InvalidOperationException("The 'each' command requires a single block argument.");
        }

        var executor = context.Runtime.BlockExecutor
                       ?? throw new InvalidOperationException("Block execution is not available in this runtime.");

        await foreach (var item in context.Input.WithCancellation(context.CancellationToken))
        {
            foreach (var current in ExpandIterationItems(item))
            {
                var iterationValues = new List<object?>();
                var shouldBreak = false;
                var shouldContinue = false;

                try
                {
                    await foreach (var value in executor.ExecuteAsync(
                                       block,
                                       new Dictionary<string, object?>(StringComparer.Ordinal)
                                       {
                                           ["it"] = current,
                                       },
                                       context.CancellationToken)
                                       .WithCancellation(context.CancellationToken))
                    {
                        iterationValues.Add(value);
                    }
                }
                catch (ContinueSignalException)
                {
                    shouldContinue = true;
                }
                catch (BreakSignalException)
                {
                    shouldBreak = true;
                }

                foreach (var value in iterationValues)
                {
                    yield return value;
                }

                if (shouldBreak)
                {
                    yield break;
                }

                if (shouldContinue)
                {
                    continue;
                }
            }
        }
    }

    private static IEnumerable<object?> ExpandIterationItems(object? item)
    {
        if (item is null || item is string || item is not IEnumerable enumerable)
        {
            yield return item;
            yield break;
        }

        foreach (var element in enumerable)
        {
            yield return element;
        }
    }
}
