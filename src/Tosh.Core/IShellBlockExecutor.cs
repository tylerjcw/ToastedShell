namespace Tosh.Core;

public interface IShellBlockExecutor
{
    IAsyncEnumerable<object?> ExecuteAsync(
        ShellBlock block,
        IReadOnlyDictionary<string, object?> locals,
        CancellationToken cancellationToken = default);
}
