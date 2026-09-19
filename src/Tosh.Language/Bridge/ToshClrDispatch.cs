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
    /// <summary>
    /// Reads the named tōsh property and returns a value the caller's type will accept.
    /// </summary>
    /// <remarks>
    /// A widget says what it is through properties as much as through methods —
    /// <c>IsFocusable</c>, <c>Value</c>, <c>Children</c> — and a framework reads them off the
    /// object it holds. Overriding only methods left <c>prop IsFocusable = true</c> visible
    /// to the language and invisible to the platform: the widget answered <c>true</c> to a
    /// script and <c>false</c> to the thing deciding whether it could be focused, so it never
    /// received input at all.
    /// </remarks>
    public static object? GetProperty(object toshInstance, string propertyName, Type returnType)
    {
        ArgumentNullException.ThrowIfNull(toshInstance);
        ArgumentNullException.ThrowIfNull(returnType);

        var instance = (ToshClassInstance)toshInstance;

        if (!instance.TryGetMember(propertyName, out var value))
        {
            throw new InvalidOperationException(
                $"'{instance.Definition.Name}' declares no property '{propertyName}' to override with.");
        }

        if (TypeConversion.TryConvert(value, returnType, out var converted))
        {
            return converted;
        }

        throw new InvalidOperationException(
            $"'{instance.Definition.Name}.{propertyName}' holds " +
            $"{(value is null ? "nothing" : $"a {value.GetType().Name}")}, but it overrides a member " +
            $"of type {returnType.Name}.");
    }

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
