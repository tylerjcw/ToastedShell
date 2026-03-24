namespace Tosh.Core;

public interface IShellEvaluator
{
    IAsyncEnumerable<object?> EvaluateAsync(string source, string sourceName, CancellationToken cancellationToken = default);
}
