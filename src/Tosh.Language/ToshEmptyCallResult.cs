namespace Tosh.Language;

/// <summary>
/// The result of a call whose body produced no values at all — <c>TOAST-0123</c>.
/// </summary>
/// <remarks>
/// <para>
/// A free function reaches a pipeline as an <see cref="IShellCommand"/> and streams, so a
/// body that produces nothing is naturally an empty stream. A class method returns a single
/// <c>InvocationResult</c>, and <c>object?</c> has no way to say "no values" — so zero
/// values were collapsed to <see langword="null"/> and a pipeline saw one null item. Any
/// method meaning "zero or more" therefore poisoned its own pipeline on the empty case,
/// which is usually the common one.
/// </para>
/// <para>
/// This marker carries that distinction the short distance from the invocation to the
/// pipeline head, which is the only place it means anything. Every other reader sees
/// <see langword="null"/>: <c>EvaluateArgumentAsync</c> unwraps it, so
/// <c>var x = ($e.Loop(0))</c> binds null exactly as it always has. The marker is never a
/// value a script can hold or name.
/// </para>
/// </remarks>
internal sealed class ToshEmptyCallResult
{
    private ToshEmptyCallResult()
    {
    }

    /// <summary>The single instance; identity is the whole of it.</summary>
    internal static ToshEmptyCallResult Instance { get; } = new();

    /// <summary>Maps the marker to null, leaving every other value alone.</summary>
    internal static object? Unwrap(object? value)
        => ReferenceEquals(value, Instance) ? null : value;

    public override string ToString() => string.Empty;
}
