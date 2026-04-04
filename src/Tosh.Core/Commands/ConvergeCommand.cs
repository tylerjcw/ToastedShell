namespace Tosh.Core.Commands;

public sealed class ConvergeCommand : ShellCommand
{
    public ConvergeCommand()
        : base("converge", "Applies a callable to the seed repeatedly until two consecutive results are equal, then yields that stable value.", "converge <seed> <callable|block>") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (context.Arguments.Count != 2)
        {
            throw context.CreateDiagnostic(
                code: "tosh::runtime::converge_requires_seed_and_callable",
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
