namespace Tosh.Core;

public static class AsyncEnumerableExtensions
{
    public static async IAsyncEnumerable<T> Empty<T>()
    {
        yield break;
    }

    public static async IAsyncEnumerable<T> FromEnumerable<T>(IEnumerable<T> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        foreach (var value in values)
        {
            yield return value;
        }
    }

    public static async Task<List<T>> ToListAsync<T>(this IAsyncEnumerable<T> source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        var values = new List<T>();

        await foreach (var item in source.WithCancellation(cancellationToken))
        {
            values.Add(item);
        }

        return values;
    }
}
