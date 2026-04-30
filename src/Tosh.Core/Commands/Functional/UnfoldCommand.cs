namespace Tosh.Core.Commands.Functional;

[Stdlib(StdlibCategory.Functional)]
[CommandCategory("Functional")]
[CommandArgument("seed", "The initial state passed to the callable.")]
[CommandArgument("callable|block", "A function that receives state and returns [value, next-state] or null to stop.")]
[CommandExample("unfold 1 func(n) => if ($n <= 5) { [$n ($n + 1)] } else { null }", Title = "Generate 1 through 5")]
[CommandExample("unfold [0 1] func(s) => [($s[0]) [($s[1]) ($s[0] + $s[1])]]", Title = "Fibonacci sequence")]
[CommandOutput("A sequence of values produced by the callable until it returns null.")]
public sealed class UnfoldCommand : ShellCommand
{
    public UnfoldCommand()
        : base("unfold", "Generates values from a seed by repeatedly applying a callable. The callable receives the current state and must return a [value, next-state] pair, or null to stop.", "unfold <seed> <callable|block>") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (context.Arguments.Count != 2)
        {
            throw context.CreateDiagnostic(
                code: "tosh.runtime.unfold_requires_seed_and_callable",
                title: "'unfold' requires a seed value and a callable value or block.",
                label: "use 'unfold <seed> func(state) => [value, next-state]'");
        }

        var state = context.Arguments[0];
        var operation = FunctionalCommandUtilities.RequireCallableOrBlock(context, 1);

        while (true)
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            var result = await FunctionalCommandUtilities.RequireSingleResultAsync(
                context,
                operation,
                [state],
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["_"] = state,
                });

            if (result is null)
            {
                yield break;
            }

            object? emitValue;
            object? nextState;

            if (result is object?[] pair && pair.Length == 2)
            {
                emitValue = pair[0];
                nextState = pair[1];
            }
            else if (result is Array typedPair && typedPair.Length == 2)
            {
                emitValue = typedPair.GetValue(0);
                nextState = typedPair.GetValue(1);
            }
            else
            {
                throw context.CreateDiagnostic(
                    code: "tosh.runtime.unfold_requires_pair_or_null",
                    title: "'unfold' callable must return a [value, next-state] pair or null to stop.",
                    label: "this result is not a two-element array or null");
            }

            yield return emitValue;
            state = nextState;
        }
    }
}
