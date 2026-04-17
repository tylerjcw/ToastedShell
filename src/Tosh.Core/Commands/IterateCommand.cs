namespace Tosh.Core.Commands;

[CommandCategory("Functional")]
[CommandArgument("seed", "The initial value of the sequence.")]
[CommandArgument("callable|block", "A function applied to the previous value to produce the next.")]
[CommandExample("iterate 1 func(x) => ($x * 2) | first 10", Title = "Powers of 2")]
[CommandExample("iterate 0 { _ + 1 } | take-until { _ > 5 }", Title = "Counting sequence bounded by take-until")]
[CommandNote("Produces an infinite sequence. Always pair with `first`, `take-until`, or `take-while` to bound the output.")]
[CommandOutput("An infinite sequence: seed, f(seed), f(f(seed)), ...")]
public sealed class IterateCommand : ShellCommand
{
    public IterateCommand()
        : base("iterate", "Generates an infinite sequence by repeatedly applying a callable to the previous result, starting from a seed. Pair with 'take' or 'take-while' to bound the output.", "iterate <seed> <callable|block>") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (context.Arguments.Count != 2)
        {
            throw context.CreateDiagnostic(
                code: "tosh::runtime::iterate_requires_seed_and_callable",
                title: "'iterate' requires a seed value and a callable value or block.",
                label: "use 'iterate <seed> func(x) => (next-value)'");
        }

        var current = context.Arguments[0];
        var operation = FunctionalCommandUtilities.RequireCallableOrBlock(context, 1);

        while (true)
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            yield return current;

            current = await FunctionalCommandUtilities.RequireSingleResultAsync(
                context,
                operation,
                [current],
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["_"] = current,
                });
        }
    }
}
