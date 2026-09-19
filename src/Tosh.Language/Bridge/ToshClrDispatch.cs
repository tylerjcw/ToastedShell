using Tosh.Runtime;

namespace Tosh.Language.Bridge;

/// <summary>
/// The call an emitted subclass's override makes to reach the tōsh method behind it.
/// </summary>
/// <remarks>
/// Held apart from the factory because emitted IL names it directly: this is the one method
/// every generated override calls, so its signature is part of the emitted contract and
/// changing it changes every type the factory has produced.
/// </remarks>
public static class ToshClrDispatch
{
    /// <summary>
    /// Runs the named tōsh method and returns a value the caller's return type will accept.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The caller is a CLR virtual, so this must run to completion on this thread and hand
    /// back a real value — there is nothing to await into. A tōsh method knows the engine
    /// that defined it and so can run itself, which is the same route
    /// <see cref="ISelfHostedCallable"/> takes for a handler assigned to an event.
    /// </para>
    /// <para>
    /// A result that will not convert is an error at the call, not a silent default. The
    /// alternative is handing a CLR caller a zero it will treat as an answer.
    /// </para>
    /// </remarks>
    public static object? Invoke(object toshInstance, string methodName, object?[] arguments, Type returnType)
    {
        ArgumentNullException.ThrowIfNull(toshInstance);
        ArgumentNullException.ThrowIfNull(returnType);

        var instance = (ToshClassInstance)toshInstance;
        var result = instance.InvokeMethodOnThisThread(methodName, arguments);

        if (returnType == typeof(void))
        {
            return null;
        }

        if (TypeConversion.TryConvert(result, returnType, out var converted))
        {
            return converted;
        }

        throw new InvalidOperationException(
            $"'{instance.Definition.Name}.{methodName}' produced " +
            $"{(result is null ? "nothing" : $"a {result.GetType().Name}")}, but it overrides a member " +
            $"returning {returnType.Name}.");
    }
}
