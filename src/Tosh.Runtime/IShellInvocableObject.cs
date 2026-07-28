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
}
