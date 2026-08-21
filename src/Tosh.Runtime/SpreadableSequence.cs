namespace Tosh.Runtime;

/// <summary>
/// A pipeline stream whose producer says the collection it carries is a *sequence*.
/// </summary>
/// <remarks>
/// <para>
/// `TOAST-0028`. The counterpart to <see cref="PreExpandedSequence"/>, and the half that
/// was missing. That marker says "these elements are already spread, do not spread them
/// again"; this one says "this value is a sequence, spreading it is what was meant".
/// </para>
/// <para>
/// Together they replace the rule they were both working around. Shape used to be decided
/// by *counting*: a collection arriving as the only item was expanded, and a collection
/// arriving beside others was not. That made a collection's meaning depend on how many of
/// them there were, so a generator yielding one array and the same generator yielding two
/// handed downstream a different shape for the same first value.
/// </para>
/// <para>
/// With both markers present the producer decides and the consumer obeys. A collection
/// literal, a variable holding one, or a range is a sequence and is marked. A command or a
/// generator yielding a collection yields it as a *value*, unmarked, and it stays one item
/// however many follow it.
/// </para>
/// <para>
/// The mark also removes the over-pull. Deciding "is this the only item?" required reading
/// one item further than the consumer asked for, so `gen | first 1` resumed the generator
/// twice — a real extra unit of work for an expensive producer, and a real error if the
/// surplus step raised. Nothing needs to look ahead to read a mark.
/// </para>
/// </remarks>
public sealed class SpreadableSequence(IAsyncEnumerable<object?> inner) : IAsyncEnumerable<object?>
{
    public IAsyncEnumerator<object?> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        => inner.GetAsyncEnumerator(cancellationToken);
}
