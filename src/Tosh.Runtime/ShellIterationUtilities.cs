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
    private static bool IsExpandableForIteration(object? value)
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

        // Not a tree. Replay as single-collection expansion.
        var hasMore = await enumerator.MoveNextAsync();

        if (!hasMore)
        {
            await enumerator.DisposeAsync();
            return (null, ExpandIterationItemsAsync(first, cancellationToken));
        }

        var replayed = ReplayAsync(first, enumerator.Current, enumerator, cancellationToken);
        return (null, replayed);
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
