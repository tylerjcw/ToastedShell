namespace Tosh.Runtime;

/// <summary>
/// Constructs values and invokes members without prescribing how a host implements dispatch.
/// </summary>
/// <remarks>
/// <para>
/// Tōast depends on this contract; the .NET host supplies <see cref="ReflectionInvoker"/>.
/// Keeping reflection in the implementation means a native host can supply its own object
/// model without changing <see cref="ToastRuntime"/> or the language engine.
/// </para>
/// <para>
/// The extension resolver remains part of the contract because extensions are lexical
/// language state. The engine supplies the resolver, while the host-specific invoker decides
/// when ordinary member lookup has failed and an extension should be consulted.
/// </para>
/// </remarks>
public interface IObjectInvoker
{
    Func<object, string, IReadOnlyList<object?>, CancellationToken, ValueTask<InvocationResult?>>?
        ExtensionResolver { get; set; }

    object CreateInstance(Type type, IReadOnlyList<object?> arguments);

    ValueTask<object> CreateInstanceAsync(
        Type type,
        IReadOnlyList<object?> arguments,
        CancellationToken cancellationToken);

    object CreateInstance(IShellStaticType type, IReadOnlyList<object?> arguments);

    ValueTask<object> CreateInstanceAsync(
        IShellStaticType type,
        IReadOnlyList<object?> arguments,
        CancellationToken cancellationToken);

    bool HasInstanceMethod(object target, string methodName);

    InvocationResult InvokeInstance(
        object target,
        string methodName,
        IReadOnlyList<object?> arguments);

    ValueTask<InvocationResult> InvokeInstanceMethodAsync(
        object target,
        string methodName,
        IReadOnlyList<object?> arguments,
        CancellationToken cancellationToken);

    ValueTask<InvocationResult> InvokeInstanceMethodAsync(
        object target,
        string methodName,
        IReadOnlyList<object?> arguments,
        IReadOnlyList<Type>? typeArguments,
        CancellationToken cancellationToken);

    InvocationResult InvokeStatic(
        Type type,
        string methodName,
        IReadOnlyList<object?> arguments);

    ValueTask<InvocationResult> InvokeStaticMethodAsync(
        Type type,
        string methodName,
        IReadOnlyList<object?> arguments,
        CancellationToken cancellationToken);

    ValueTask<InvocationResult> InvokeStaticMethodAsync(
        Type type,
        string methodName,
        IReadOnlyList<object?> arguments,
        IReadOnlyList<Type>? typeArguments,
        CancellationToken cancellationToken);

    object? GetStaticMember(Type type, string memberName);

    InvocationResult InvokeStatic(
        IShellStaticType type,
        string methodName,
        IReadOnlyList<object?> arguments);

    ValueTask<InvocationResult> InvokeStaticMethodAsync(
        IShellStaticType type,
        string methodName,
        IReadOnlyList<object?> arguments,
        CancellationToken cancellationToken);

    object? GetStaticMember(IShellStaticType type, string memberName);
}
