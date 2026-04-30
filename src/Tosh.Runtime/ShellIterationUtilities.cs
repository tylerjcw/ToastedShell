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
            var expanded = ExpandIterationItems(first);
            return (null, WrapSync(expanded));
        }

        var replayed = ReplayAsync(first, enumerator.Current, enumerator, cancellationToken);
        return (null, replayed);
    }

    private static async IAsyncEnumerable<object?> EmptyAsync()
    {
        await Task.CompletedTask;
        yield break;
    }

    private static async IAsyncEnumerable<object?> WrapSync(IEnumerable<object?> items)
    {
        foreach (var item in items)
        {
            yield return item;
        }

        await Task.CompletedTask;
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

        if (!await enumerator.MoveNextAsync())
        {
            foreach (var element in ExpandIterationItems(first))
            {
                cancellationToken.ThrowIfCancellationRequested();
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
