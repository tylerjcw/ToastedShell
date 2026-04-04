namespace Tosh.Core.Commands;

public sealed class InterleaveCommand : ShellCommand
{
    public InterleaveCommand()
        : base("interleave", "Alternates items from the pipeline with items from another sequence.", "interleave <other-sequence>") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (context.Arguments.Count != 1)
        {
            throw context.CreateDiagnostic(
                code: "tosh::runtime::interleave_requires_sequence",
                title: "'interleave' requires exactly one array argument.",
                label: "use 'interleave <array>'");
        }

        var otherItems = ResolveSequence(context, context.Arguments[0]);
        var otherIndex = 0;

        await foreach (var item in ShellIterationUtilities.ReplaySingleInputCollectionAsync(context.Input, context.CancellationToken)
                           .WithCancellation(context.CancellationToken))
        {
            yield return item;

            if (otherIndex < otherItems.Count)
            {
                yield return otherItems[otherIndex++];
            }
        }

        // Drain remaining items from the other sequence
        while (otherIndex < otherItems.Count)
        {
            yield return otherItems[otherIndex++];
        }
    }

    private static IReadOnlyList<object?> ResolveSequence(CommandContext context, object? argument)
    {
        return argument switch
        {
            object?[] array => array,
            IReadOnlyList<object?> list => list,
            string => throw context.CreateDiagnostic(
                code: "tosh::runtime::interleave_requires_sequence",
                title: "'interleave' requires an array or list as the second sequence.",
                argumentIndex: 0,
                label: "a string is not a valid sequence for interleave"),
            System.Collections.IEnumerable enumerable => enumerable.Cast<object?>().ToArray(),
            _ => throw context.CreateDiagnostic(
                code: "tosh::runtime::interleave_requires_sequence",
                title: "'interleave' requires an array or list as the second sequence.",
                argumentIndex: 0,
                label: "this value is not a sequence"),
        };
    }
}
