using Tosh.Runtime;

namespace Tosh.Stdlib.Functional;

[CommandCategory("Functional")]
[CommandArgument("seed", "The initial value to start iterating from.")]
[CommandArgument("callable|block", "A function applied repeatedly until two consecutive results are equal.")]
[CommandExample("converge 100 func(x) => ($x / 2 + 50 / $x)", Title = "Newton's method for square root of 100")]
[CommandOutput("The first stable (fixed-point) value where consecutive applications produce equal results.")]
public sealed class ConvergeCommand : ShellCommand
{
    public ConvergeCommand()
        : base("converge", "Applies a callable to the seed repeatedly until two consecutive results are equal, then yields that stable value.", "converge <seed> <callable|block>") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (context.Arguments.Count != 2)
        {
            throw context.CreateDiagnostic(
                code: "tosh.runtime.converge_requires_seed_and_callable",
                title: "'converge' requires a seed value and a callable value or block.",
                label: "use 'converge <seed> func(x) => (next-value)'");
        }

        var current = context.Arguments[0];
        var operation = FunctionalCommandUtilities.RequireCallableOrBlock(context, 1);

        while (true)
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            var next = await FunctionalCommandUtilities.RequireSingleResultAsync(
                context,
                operation,
                [current],
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["_"] = current,
                });

            if (OperatorEvaluator.AreEqual(current, next))
            {
                yield return current;
                yield break;
            }

            current = next;
        }
    }
}
