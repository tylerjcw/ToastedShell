namespace Tosh.Runtime;

/// <summary>
/// A pipeline stream whose producer already enumerated a collection into it.
/// </summary>
/// <remarks>
/// <para>
/// `TS-P2-113`. A collection reaching a pipeline gets expanded element-wise, and
/// that rule is right — it is what makes `$items | where { … }` read the way it
/// does. The trouble is that it was being applied twice to the same value.
/// </para>
/// <para>
/// A variable used as a pipeline stage is enumerated at the stage, so `$r` where
/// `r = [[1, 2, 3]]` correctly produces one item: the inner array. Downstream,
/// `ReplaySingleInputCollectionAsync` sees a stream of exactly one collection and
/// expands it again, so `$r | count` answered 3 where the identical literal
/// `[[1, 2, 3]] | count` answered 1. `$r | first` came back an `Int32` rather than
/// an `Int32[]`, and `for x in $r` bound three integers instead of one array.
/// </para>
/// <para>
/// The two readings cannot be told apart from the stream alone — "one collection
/// that should be spread" and "one collection that is genuinely the item" look
/// identical — so the producer says which. Marking the stream is deliberately
/// preferred over removing the stage-level enumeration: commands such as `where`,
/// `sort`, `to` and `join` never call the replay helper and rely on receiving the
/// elements, so removing it would have fixed the count and broken the filter.
/// </para>
/// </remarks>
public sealed class PreExpandedSequence(IAsyncEnumerable<object?> inner) : IAsyncEnumerable<object?>
{
    public IAsyncEnumerator<object?> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        => inner.GetAsyncEnumerator(cancellationToken);
}
