namespace Tosh.Core.Commands;

[CommandCategory("Functional")]
[CommandArgument("seeds", "The initial values of the recurrence, e.g. (0, 1) for Fibonacci.")]
[CommandArgument("callable|block", "A function applied to the last N values to produce the next. N matches the seed count.")]
[CommandExample("recur (0, 1) func(a, b) => ($a + $b) | first 10", Title = "Fibonacci sequence")]
[CommandExample("recur (1, 1, 1) func(a, b, c) => ($a + $b + $c) | first 10", Title = "Tribonacci sequence")]
[CommandNote("Produces an infinite sequence. Always pair with `first`, `take-while`, or `take-until` to bound the output.")]
[CommandOutput("An infinite sequence: seeds..., f(seeds), f(seed[1..], f(seeds)), ...")]
public sealed class RecurCommand : ShellCommand
{
    public RecurCommand()
        : base("recur", "Generates an infinite sequence from seed values using a recurrence relation. The callable receives the last N values (matching seed count) and returns the next value.", "recur <seeds> <callable|block>") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (context.Arguments.Count != 2)
        {
            throw context.CreateDiagnostic(
                code: "tosh::runtime::recur_requires_seeds_and_callable",
                title: "'recur' requires seed values and a callable.",
                label: "use 'recur (seed1, seed2) func(a, b) => (next-value)'");
        }

        // Extract seed values from the first argument (tuple, array, or single value)
        var seedArg = context.Arguments[0];
        var window = ExtractSeeds(context, seedArg);

        if (window.Count == 0)
        {
            throw context.CreateDiagnostic(
                code: "tosh::runtime::recur_empty_seeds",
                title: "'recur' requires at least one seed value.",
                label: "provide initial values for the recurrence");
        }

        var operation = FunctionalCommandUtilities.RequireCallableOrBlock(context, 1);

        // Yield all seed values first
        foreach (var seed in window)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            yield return seed;
        }

        // Generate subsequent values via the recurrence relation
        while (true)
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            var next = await FunctionalCommandUtilities.RequireSingleResultAsync(
                context,
                operation,
                window,
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["_"] = window.Count == 1 ? window[0] : window,
                });

            yield return next;

            // Slide the window forward
            var newWindow = new List<object?>(window.Count);
            for (var i = 1; i < window.Count; i++)
            {
                newWindow.Add(window[i]);
            }
            newWindow.Add(next);
            window = newWindow;
        }
    }

    private static List<object?> ExtractSeeds(CommandContext context, object? seedArg)
    {
        if (seedArg is object?[] arr)
        {
            return new List<object?>(arr);
        }

        if (seedArg is Array typedArr)
        {
            var seeds = new List<object?>(typedArr.Length);
            foreach (var item in typedArr)
            {
                seeds.Add(item);
            }
            return seeds;
        }

        if (seedArg is IReadOnlyList<object?> readOnlyList && seedArg is not string)
        {
            var seeds = new List<object?>(readOnlyList.Count);
            for (var i = 0; i < readOnlyList.Count; i++)
            {
                seeds.Add(readOnlyList[i]);
            }
            return seeds;
        }

        if (seedArg is System.Collections.IList list && seedArg is not string)
        {
            var seeds = new List<object?>(list.Count);
            foreach (var item in list)
            {
                seeds.Add(item);
            }
            return seeds;
        }

        if (seedArg is IShellEnumerableObject shellEnum)
        {
            return new List<object?>(shellEnum.EnumerateShellItems());
        }

        // Single value seed
        return [seedArg];
    }
}
