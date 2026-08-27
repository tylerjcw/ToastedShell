namespace Tosh.Runtime;

/// <summary>
/// Marks a compiled module method whose single <c>object[]</c> parameter is the whole
/// argument list — <c>TOAST-0035</c>.
/// </summary>
/// <remarks>
/// <para>
/// A tosh function with an optional, rest, or defaulted parameter cannot be a CLR method
/// with one parameter per tosh parameter: a default may be any expression, and the value has
/// to be produced where the expression can be evaluated — inside the body. So the emitted
/// method takes the arguments packed into an array, substitutes a sentinel for what the
/// caller omitted, and fills the defaults itself.
/// </para>
/// <para>
/// The marker exists because the shape is not inferable from the signature. A method taking
/// one <c>object[]</c> is exactly what a tosh function of one array-typed parameter would
/// also emit, and <c>ToshHost.TryInvokeCompiledModuleMethod</c> has to tell the two apart to
/// decide whether to pass the caller's arguments through or wrap them.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Method)]
public sealed class ToshPackedArgumentsAttribute : Attribute
{
    public ToshPackedArgumentsAttribute(int parameterCount) => ParameterCount = parameterCount;

    /// <summary>How many tosh parameters the packed array stands for.</summary>
    public int ParameterCount { get; }
}
