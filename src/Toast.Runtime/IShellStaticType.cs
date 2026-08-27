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

    /// <summary>
    /// Stores <paramref name="value"/> in the static member named <paramref name="memberName"/>,
    /// or answers <see langword="false"/> when this type has no such writable member.
    /// </summary>
    /// <remarks>
    /// Defaults to <see langword="false"/> — "nothing here can be written" — because most shell
    /// types expose statics that are constants, methods, or nested declarations. A type that
    /// stores static state overrides this; refusing a specific member is that type's own
    /// business and belongs in an exception with a reason, not in this answer.
    /// </remarks>
    bool TrySetStaticMember(string memberName, object? value) => false;
}
