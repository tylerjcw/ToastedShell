namespace Tosh.Runtime;

public interface IShellInvocableObject
{
    InvocationResult InvokeInstanceMethod(string methodName, IReadOnlyList<object?> arguments);
}
