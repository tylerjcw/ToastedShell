namespace Tosh.Core;

public interface IShellStaticType
{
    string ShellTypeName { get; }

    object CreateInstance(IReadOnlyList<object?> arguments);

    InvocationResult InvokeStaticMethod(string methodName, IReadOnlyList<object?> arguments);

    bool TryGetStaticMember(string memberName, out object? value);
}
