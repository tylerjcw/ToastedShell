namespace Tosh.Runtime;

public interface IShellStaticType
{
    string ShellTypeName { get; }

    object CreateInstance(IReadOnlyList<object?> arguments);

    InvocationResult InvokeStaticMethod(string methodName, IReadOnlyList<object?> arguments);

    ValueTask<object> CreateInstanceAsync(
        IReadOnlyList<object?> arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(CreateInstance(arguments));
    }

    ValueTask<InvocationResult> InvokeStaticMethodAsync(
        string methodName,
        IReadOnlyList<object?> arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(InvokeStaticMethod(methodName, arguments));
    }

    bool TryGetStaticMember(string memberName, out object? value);
}
