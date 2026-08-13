using Tosh.Runtime;

namespace Tosh.Language;

/// <summary>
/// A first-class reference to a class's static method, produced by
/// <c>&amp;C.Method</c>.
/// </summary>
/// <remarks>
/// <para>
/// `TS-P2-94`. A module-qualified function already evaluated to a callable, so
/// <c>&amp;M.Exported</c> only needed the parser to admit the dot. A static
/// method has no such value — reading <c>C.Method</c> is deliberately an error
/// telling you to call it with parentheses — so referencing one needs this
/// wrapper.
/// </para>
/// <para>
/// It stands for the whole overload set rather than one signature, which is why
/// the arity is reported as unbounded: choosing among overloads is the
/// dispatcher's job at call time, using the arguments actually supplied, and
/// duplicating that choice here would be a second, weaker resolver — the
/// `TS-P1-24` failure mode. Arity and ambiguity errors therefore come from the
/// same place they come from for a direct call.
/// </para>
/// </remarks>
internal sealed class ToshStaticMethodReference(ToshClassDefinition definition, string methodName)
    : IShellCallable
{
    public string CallableName { get; } = $"{definition.Name}.{methodName}";

    public int RequiredParameterCount => 0;

    public int? MaximumParameterCount => null;

    public async IAsyncEnumerable<object?> InvokeAsync(CommandContext context)
    {
        var result = await definition.InvokeStaticMethodAsync(
            methodName,
            context.Arguments,
            context.CancellationToken);

        if (!result.ReturnedVoid)
        {
            yield return result.Value;
        }
    }
}
