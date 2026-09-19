using Tosh.Runtime;

namespace Tosh.Language;

/// <summary>
/// A method reference bound to a receiver — <c>&amp;$obj.Method</c>.
/// </summary>
/// <remarks>
/// <para>
/// `TS-P2-94`, the half left unbuilt when the rest landed. `&amp;Type.Method`,
/// `&amp;Module.func` and `&amp;name` all resolve to something that already exists: a
/// registered command, a module member, or a static method wrapped in
/// <see cref="ToshStaticMethodReference"/>. A bound instance reference does not — it has
/// to carry the receiver *and* the method, which is why it was filed rather than
/// half-built.
/// </para>
/// <para>
/// The receiver is captured when the reference is taken, not when it is called. That
/// matches every other language with bound method references and is the only reading
/// that makes `&amp;$obj.Method` useful: handing the callable somewhere else is the point,
/// and a reference that re-read the variable would follow it to a different object.
/// </para>
/// <para>
/// Dispatch splits by receiver kind because the two paths are genuinely different.
/// A <see cref="IShellInvocableObject"/> knows its own methods, including ToastScript
/// classes with their overload and visibility rules; anything else is a CLR object and
/// goes through the invoker. Trying the instance path first matters: a ToastScript class
/// instance is also a CLR object, and reflecting over it would find the implementation
/// type's members rather than the declared ones.
/// </para>
/// </remarks>
internal sealed class ToshBoundMethodReference(
    object receiver,
    string methodName,
    IObjectInvoker invoker,
    ToshEngine engine) : IShellCallable, ISelfHostedCallable
{
    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Without this a bound method reference could be passed everywhere inside the language
    /// and nowhere outside it: handing `&amp;$obj.Method` to anything wanting a delegate
    /// failed overload resolution, while the lambda beside it — identical call, identical
    /// signature — sorted the list. The only difference was that
    /// <see cref="ToshLambda"/> could run itself and this could not.
    /// </para>
    /// <para>
    /// Handing the callable somewhere else is the entire point of taking a reference to it,
    /// and "somewhere else" includes the platform.
    /// </para>
    /// </remarks>
    public object? InvokeWithoutContext(IReadOnlyList<object?> arguments)
        => engine.InvokeCallableOnThisThread(this, arguments);

    public string CallableName { get; } = methodName;

    public int RequiredParameterCount => 0;

    public int? MaximumParameterCount => null;

    public async IAsyncEnumerable<object?> InvokeAsync(CommandContext context)
    {
        var result = receiver is IShellInvocableObject instance
            ? await instance.InvokeInstanceMethodAsync(CallableName, context.Arguments, context.CancellationToken)
            : invoker.InvokeInstance(receiver, CallableName, context.Arguments);

        if (!result.ReturnedVoid)
        {
            yield return result.Value;
        }
    }
}
