namespace Tosh.Core.Commands.Functional;

[Stdlib(StdlibCategory.Functional)]
[CommandCategory("Functional")]
[CommandArgument("callable|block", "A block or function to evaluate each time. Receives the current index as $_ and as the first argument.")]
[CommandExample("repeatedly { random int 1 100 } | first 5", Title = "Five random numbers")]
[CommandExample("repeatedly func(i) => ($i * $i) | first 6", Title = "Squares via index")]
[CommandNote("Produces an infinite sequence. Always pair with `first`, `take-while`, or `take-until` to bound the output.")]
[CommandNote("Unlike `repeat`, this re-evaluates the block for each item, so side effects and randomness work.")]
[CommandOutput("Infinite sequence of evaluated block results.")]
public sealed class RepeatedlyCommand : ShellCommand
{
    public RepeatedlyCommand()
        : base("repeatedly", "Produces an infinite sequence by re-evaluating a block for each item. The block receives the 0-based index.", "repeatedly <callable|block>") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (context.Arguments.Count != 1)
        {
            throw context.CreateDiagnostic(
                code: "tosh.runtime.repeatedly_requires_callable",
                title: "'repeatedly' requires a callable or block.",
                label: "use 'repeatedly { expression }'");
        }

        var operation = FunctionalCommandUtilities.RequireCallableOrBlock(context, 0);

        for (long i = 0; ; i++)
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            var result = await FunctionalCommandUtilities.RequireSingleResultAsync(
                context,
                operation,
                [i],
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["_"] = i,
                });

            yield return result;
        }
    }
}
