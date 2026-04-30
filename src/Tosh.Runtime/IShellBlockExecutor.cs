namespace Tosh.Runtime;

public interface IShellBlockExecutor
{
    IAsyncEnumerable<object?> ExecuteAsync(
        ShellBlock block,
        IReadOnlyDictionary<string, object?> locals,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Invokes a callable, routing execution through the (potentially forked) engine that
    /// owns this executor rather than the engine the callable was originally created with.
    /// This enables true concurrency for user-defined functions in race/settle/parallel.
    /// Default implementation delegates directly to <see cref="IShellCallable.InvokeAsync"/>.
    /// </summary>
    IAsyncEnumerable<object?> InvokeCallableAsync(IShellCallable callable, CommandContext context)
        => callable.InvokeAsync(context);

    /// <summary>
    /// Creates a new executor with an isolated scope snapshot of the current execution
    /// state. The fork shares the same <see cref="ToshRuntime"/> but has its own lexical
    /// scope stack, function-call stacks, and block executor — enabling safe concurrent
    /// block and callable execution.
    /// Default implementation returns <c>this</c> (no isolation — safe for non-concurrent use).
    /// </summary>
    IShellBlockExecutor Fork() => this;
}
