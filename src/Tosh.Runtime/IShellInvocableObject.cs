namespace Tosh.Runtime;

public interface IShellInvocableObject
{
    InvocationResult InvokeInstanceMethod(string methodName, IReadOnlyList<object?> arguments);

    ValueTask<InvocationResult> InvokeInstanceMethodAsync(
        string methodName,
        IReadOnlyList<object?> arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(InvokeInstanceMethod(methodName, arguments));
    }

    /// <summary>
    /// Invokes a method with type arguments written at the call site —
    /// <c>$a.m&lt;int&gt;(11)</c>.
    /// </summary>
    /// <remarks>
    /// The default refuses rather than ignores. An implementation that does not understand type
    /// arguments must say so: silently dropping them would bind by inference and return a
    /// plausible answer to a question the caller did not ask, which is worse than the parse
    /// error this feature replaced. Implementations that do understand them override this.
    /// </remarks>
    ValueTask<InvocationResult> InvokeInstanceMethodAsync(
        string methodName,
        IReadOnlyList<object?> arguments,
        IReadOnlyList<Type>? typeArguments,
        CancellationToken cancellationToken)
    {
        if (typeArguments is { Count: > 0 })
        {
            throw new InvalidOperationException(
                $"Method '{methodName}' on '{GetType().Name}' does not accept explicit type arguments.");
        }

        return InvokeInstanceMethodAsync(methodName, arguments, cancellationToken);
    }
}
