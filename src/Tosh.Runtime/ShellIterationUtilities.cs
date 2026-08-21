using System.Collections;
using System.Runtime.CompilerServices;

namespace Tosh.Runtime;

public static class ShellIterationUtilities
{
    public static IEnumerable<object?> ExpandIterationItems(object? item)
    {
        if (item is IShellEnumerableObject shellEnumerable)
        {
            foreach (var element in shellEnumerable.EnumerateShellItems())
            {
                yield return element;
            }

            yield break;
        }

        foreach (var element in ExpandCollectionLikeValue(item))
        {
            yield return element;
        }
    }

    public static async IAsyncEnumerable<object?> ExpandIterationItemsAsync(
        object? item,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (item is IShellEnumerableObject shellEnumerable)
        {
            await foreach (var element in shellEnumerable
                               .EnumerateShellItemsAsync(cancellationToken)
                               .WithCancellation(cancellationToken))
            {
                yield return element;
            }

            yield break;
        }

        foreach (var element in ExpandCollectionLikeValue(item))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return element;
        }
    }

    /// <summary>
    /// Whether expanding <paramref name="value"/> could yield anything other than the
    /// value itself. Mirrors <see cref="ExpandCollectionLikeValue"/>'s own rule — null,
    /// strings, and record-likes are atoms, and so is anything that is not
    /// <see cref="IEnumerable"/>.
    /// </summary>
    /// <remarks>
    /// Kept beside the expansion it mirrors, because the two disagreeing would put the
    /// lookahead back for values that do not need it or remove it for values that do.
    /// </remarks>
    /// <summary>
    /// Whether the language treats this value as a sequence rather than an atom.
    /// </summary>
    /// <remarks>
    /// Public since `TOAST-0029`, so `is` answers the same question the pipeline does: a
    /// `str`, a record and a dictionary are single values (`§Collection Shape`), and a
    /// bare `is IEnumerable` has to agree with that or the two would contradict each other
    /// about what a string is. Sharing the predicate is what keeps them in step.
    /// </remarks>
    public static bool IsExpandableForIteration(object? value)
    {
        if (value is IShellEnumerableObject)
        {
            return true;
        }

        return value is not null
            && value is not string
            && !ShellRecordUtilities.IsRecordLike(value)
            && value is IEnumerable;
    }

    public static IEnumerable<object?> ExpandCollectionLikeValue(object? value)
    {
        if (value is null || value is string || ShellRecordUtilities.IsRecordLike(value))
        {
            yield return value;
            yield break;
        }

        if (value is not IEnumerable enumerable)
        {
            yield return value;
            yield break;
        }

        foreach (var element in enumerable)
        {
            yield return element;
        }
    }

    public static async Task<(TreeEntryInfo? Tree, IAsyncEnumerable<object?> Items)> PeekForTreeAsync(
        IAsyncEnumerable<object?> input,
        CancellationToken cancellationToken)
    {
        var enumerator = input.GetAsyncEnumerator(cancellationToken);

        if (!await enumerator.MoveNextAsync())
        {
            await enumerator.DisposeAsync();
            return (null, EmptyAsync());
        }

        var first = enumerator.Current;

        if (first is TreeEntryInfo tree && tree.Children.Count > 0)
        {
            var hasSecond = await enumerator.MoveNextAsync();

            if (!hasSecond)
            {
                await enumerator.DisposeAsync();
                return (tree, EmptyAsync());
            }

            // Multiple items — not a single tree root. Replay everything.
            var items = ReplayAsync(first, enumerator.Current, enumerator, cancellationToken);
            return (null, items);
        }

        // `TOAST-0028` stage 1. Not a tree, so the shape question belongs to the one place
        // that answers it. This used to repeat `ReplaySingleInputCollectionAsync`'s rule
        // inline — pull a second item, expand a lone collection, otherwise replay — which
        // made one rule exist twice.
        //
        // Copying it also copied it incompletely: `TS-P2-113` taught the *other* copy to
        // honour `PreExpandedSequence`, and this one never learned. So for
        // `var r = [[1, 2, 3]]`, `$r | count` answered 1 while `$r | where true | count`
        // answered 3 — the same stream, two answers, because the marker saying "already
        // expanded" was read by one path and not the other.
        //
        // The marker is carried across the replay deliberately: what was consumed to look
        // for a tree has to be handed back *as the same kind of stream*, or delegating
        // would lose the very thing being fixed.
        var remainder = ReplayFromAsync(first, enumerator, cancellationToken);

        return (null, ReplaySingleInputCollectionAsync(
            CarryShapeMarker(input, remainder),
            cancellationToken));
    }

    /// <summary>
    /// Re-applies whichever shape marker <paramref name="original"/> carried.
    /// </summary>
    /// <remarks>
    /// Wrapping a stream in another iterator erases its type, and the type is the whole
    /// signal. Every place that hands a marked stream onward has to put the mark back or
    /// the producer's decision is lost in transit — which is exactly how `PeekForTreeAsync`
    /// came to disagree with the helper it was copied from.
    /// </remarks>
    public static IAsyncEnumerable<object?> CarryShapeMarker(
        IAsyncEnumerable<object?> original,
        IAsyncEnumerable<object?> rewrapped)
        => original switch
        {
            PreExpandedSequence => new PreExpandedSequence(rewrapped),
            SpreadableSequence => new SpreadableSequence(rewrapped),
            _ => rewrapped,
        };

    /// <summary>Yields an item already taken from an enumerator, then the rest of it.</summary>
    private static async IAsyncEnumerable<object?> ReplayFromAsync(
        object? first,
        IAsyncEnumerator<object?> remaining,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return first;

        try
        {
            while (await remaining.MoveNextAsync())
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return remaining.Current;
            }
        }
        finally
        {
            await remaining.DisposeAsync();
        }
    }

    private static async IAsyncEnumerable<object?> EmptyAsync()
    {
        await Task.CompletedTask;
        yield break;
    }

    private static async IAsyncEnumerable<object?> ReplayAsync(
        object? first,
        object? second,
        IAsyncEnumerator<object?> remaining,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return first;
        yield return second;

        while (await remaining.MoveNextAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return remaining.Current;
        }

        await remaining.DisposeAsync();
    }

    public static async IAsyncEnumerable<object?> ReplaySingleInputCollectionAsync(
        IAsyncEnumerable<object?> input,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // `TS-P2-113`. A stream whose producer already enumerated a collection into
        // it must not be expanded a second time: the lone item it carries is the
        // item, not a container to spread.
        if (input is PreExpandedSequence)
        {
            await foreach (var value in input.WithCancellation(cancellationToken))
            {
                yield return value;
            }

            yield break;
        }

        // `TOAST-0028`. The producer decides, and an unmarked producer has decided that
        // the collection it yielded is a *value*.
        //
        // This is the rule that used to be inferred by counting: a collection arriving
        // alone was spread and a collection arriving beside others was not, so a generator
        // yielding one array and the same generator yielding two handed downstream
        // different shapes for the same first value. Adding a second batch changed what the
        // first batch meant, and nothing at the call site could say which was intended.
        //
        // Expression heads — a literal, a variable, a range — are marked
        // `SpreadableSequence` and keep the old behaviour exactly. What changes is a
        // command or a user generator yielding a collection: it is now one item, however
        // many follow it. `...` is the spelling for the other meaning (`TOAST-0032`), and
        // it is why this could be a migration rather than a cliff.
        if (input is not SpreadableSequence)
        {
            await foreach (var value in input.WithCancellation(cancellationToken))
            {
                yield return value;
            }

            yield break;
        }

        await using var enumerator = input.GetAsyncEnumerator(cancellationToken);

        if (!await enumerator.MoveNextAsync())
        {
            yield break;
        }

        var first = enumerator.Current;

        // The lookahead below exists to answer one question: is this a *lone* collection
        // that should be expanded element-wise? When the first item is not expandable the
        // answer does not matter — expanding a scalar yields the scalar — so pulling a
        // second item to find out is pure waste, and the waste is observable. A generator
        // gets resumed once more than the consumer asked for, so `gen | first 1` produced
        // two items and `gen | any { … }` did too (TS-P1-08). For an expensive or
        // side-effecting producer that is a real extra unit of work, and if the surplus
        // item throws, the error is reported for work nobody requested.
        if (!IsExpandableForIteration(first))
        {
            yield return first;

            while (await enumerator.MoveNextAsync())
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return enumerator.Current;
            }

            yield break;
        }

        if (!await enumerator.MoveNextAsync())
        {
            await foreach (var element in ExpandIterationItemsAsync(first, cancellationToken)
                               .WithCancellation(cancellationToken))
            {
                yield return element;
            }

            yield break;
        }

        yield return first;
        yield return enumerator.Current;

        while (await enumerator.MoveNextAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return enumerator.Current;
        }
    }
}
