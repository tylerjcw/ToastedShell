namespace Tosh.Core.Commands;

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
