namespace Tosh.Core.Commands;

[CommandCategory("Functional")]
public sealed class UnfoldCommand : ShellCommand
{
    public UnfoldCommand()
        : base("unfold", "Generates values from a seed by repeatedly applying a callable. The callable receives the current state and must return a [value, next-state] pair, or null to stop.", "unfold <seed> <callable|block>") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (context.Arguments.Count != 2)
        {
            throw context.CreateDiagnostic(
                code: "tosh::runtime::unfold_requires_seed_and_callable",
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

            if (result is not object?[] pair || pair.Length != 2)
            {
                throw context.CreateDiagnostic(
                    code: "tosh::runtime::unfold_requires_pair_or_null",
                    title: "'unfold' callable must return a [value, next-state] pair or null to stop.",
                    label: "this result is not a two-element array or null");
            }

            yield return pair[0];
            state = pair[1];
        }
    }
}
