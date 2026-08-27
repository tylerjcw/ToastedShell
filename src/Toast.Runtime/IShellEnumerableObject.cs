namespace Tosh.Runtime;

public interface IShellEnumerableObject
{
    /// <summary>
    /// Indicates whether this value exposes collection items rather than merely
    /// participating in shell iteration as a scalar.
    /// </summary>
    bool HasShellItems => true;

    IEnumerable<object?> EnumerateShellItems();

    async IAsyncEnumerable<object?> EnumerateShellItemsAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken)
    {
        foreach (var item in EnumerateShellItems())
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return item;
        }
    }
}
