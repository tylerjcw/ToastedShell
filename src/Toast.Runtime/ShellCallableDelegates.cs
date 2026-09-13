using System.Linq.Expressions;
using System.Reflection;

namespace Tosh.Runtime;

/// <summary>
/// Wraps a script function in a CLR delegate, so a .NET type can call back into a script.
/// </summary>
/// <remarks>
/// <para>
/// This is what makes event handlers work. A widget raises <c>Action&lt;string&gt;</c>, a
/// sort takes <c>Comparison&lt;T&gt;</c>, a timer takes <c>Action</c> — none of them will
/// ever know what an <see cref="IShellCallable"/> is, and a script should not have to care.
/// Assigning a function where a delegate is wanted now does what it looks like it does:
/// </para>
/// <code>
/// $field.Changed = func(text) => (writeline $"now: {$text}")
/// </code>
/// <para>
/// The delegate is compiled rather than reflected because it has to <em>be</em> the
/// requested signature — that is the whole point of handing it to a type that expects one.
/// A compiled lambda is what turns a fixed <c>object?[]</c> dispatcher into an arbitrary
/// signature without emitting IL by hand, and the native callback thunks already work this
/// way.
/// </para>
/// </remarks>
public static class ShellCallableDelegates
{
    private static readonly MethodInfo DispatchMethod =
        typeof(ShellCallableDelegates).GetMethod(nameof(Dispatch), BindingFlags.Static | BindingFlags.NonPublic)!;

    /// <summary>Builds a delegate of the requested type around a script callable.</summary>
    /// <returns>
    /// <see langword="false"/> when the value is not a callable that can host itself, or
    /// the delegate's signature is one that cannot be expressed as objects.
    /// </returns>
    public static bool TryCreate(object? value, Type delegateType, out Delegate? created)
    {
        ArgumentNullException.ThrowIfNull(delegateType);

        created = null;

        if (value is not ISelfHostedCallable host)
        {
            return false;
        }

        // `Delegate` and `MulticastDelegate` themselves are abstract: they name the family,
        // not a signature, and there is nothing to conform to.
        if (!typeof(Delegate).IsAssignableFrom(delegateType) ||
            delegateType == typeof(Delegate) ||
            delegateType == typeof(MulticastDelegate) ||
            delegateType.ContainsGenericParameters)
        {
            return false;
        }

        if (delegateType.GetMethod("Invoke") is not { } signature)
        {
            return false;
        }

        var parameters = signature.GetParameters();

        // Every argument is boxed into an `object?[]` on its way to the script, and a value
        // that cannot be boxed cannot make that trip. Refusing here leaves the assignment
        // reported as a type mismatch, which is true, rather than failing when the event
        // eventually fires — by which time the line that caused it is long gone.
        if (parameters.Any(parameter => IsUnboxable(parameter.ParameterType)) ||
            IsUnboxable(signature.ReturnType))
        {
            return false;
        }

        var arity = value is IShellCallable callable ? callable.MaximumParameterCount : null;

        var inputs = parameters
            .Select(parameter => Expression.Parameter(parameter.ParameterType, parameter.Name))
            .ToArray();

        // A handler that ignores its arguments should not have to declare them. The same
        // courtesy the pull bindings extend: pass what the function can actually take.
        var passed = arity is { } maximum && maximum < inputs.Length ? inputs[..maximum] : inputs;

        var boxed = Expression.NewArrayInit(
            typeof(object),
            passed.Select(input => (Expression)Expression.Convert(input, typeof(object))));

        var call = Expression.Call(
            DispatchMethod,
            Expression.Constant(host),
            boxed,
            Expression.Constant(signature.ReturnType, typeof(Type)));

        var body = signature.ReturnType == typeof(void)
            ? (Expression)Expression.Block(call, Expression.Empty())
            : Expression.Convert(call, signature.ReturnType);

        created = Expression.Lambda(delegateType, body, inputs).Compile();
        return true;
    }

    /// <summary>A type a value cannot be boxed into an <c>object?[]</c> as.</summary>
    private static bool IsUnboxable(Type type)
        => type.IsByRef || type.IsPointer || type.IsByRefLike;

    /// <summary>
    /// The one call every generated delegate makes.
    /// </summary>
    /// <remarks>
    /// A delegate whose return type is not <see cref="void"/> must produce a value of that
    /// type, so what the script produced is converted rather than cast — a handler
    /// returning <c>1</c> for a <c>Func&lt;double&gt;</c> is not a mistake. A conversion
    /// that fails throws, because the caller is a .NET type mid-operation and there is no
    /// sensible value to hand it.
    /// </remarks>
    private static object? Dispatch(ISelfHostedCallable callable, object?[] arguments, Type returnType)
    {
        var result = callable.InvokeWithoutContext(arguments);

        if (returnType == typeof(void))
        {
            return null;
        }

        if (TypeConversion.TryConvert(result, returnType, out var converted))
        {
            return converted;
        }

        throw new InvalidOperationException(
            $"The handler produced {(result is null ? "nothing" : $"a {result.GetType().Name}")}, " +
            $"but its caller requires a {returnType.Name}.");
    }
}
