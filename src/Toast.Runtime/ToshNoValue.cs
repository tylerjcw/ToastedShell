namespace Tosh.Runtime;

/// <summary>
/// The result of a compiled function that produced no value — <c>TOAST-0066</c>.
/// </summary>
/// <remarks>
/// <para>
/// A function's result reaches a pipeline as a sequence interpreted, so "produced nothing"
/// is simply an empty one and needs no representation. A compiled function returns a single
/// <c>object?</c>, which has no way to say it produced nothing — so <c>f | count</c> was 0
/// interpreted and 1 compiled, the null standing in for the absent value being counted as a
/// value.
/// </para>
/// <para>
/// The distinction cannot be decided at compile time: <c>func f(x) { if ($x) { return 1 } }</c>
/// produces one value or none depending on the branch, and the interpreter reports 1 and 0
/// respectively. So it is carried at run time.
/// </para>
/// <para>
/// It is deliberately *not* the same as returning <c>null</c>. `return null` is a value the
/// interpreter counts, and dropping nulls to fix the count would silence a function that
/// returns one on purpose.
/// </para>
/// <para>
/// Only a pipeline stage distinguishes the two. In every other position — an assignment, a
/// subexpression argument, a comparison against <c>null</c> — the interpreter reads a
/// function that produced nothing as <c>null</c>, so the sentinel is normalised away at the
/// call site and never reaches a value the reader can hold.
/// </para>
/// </remarks>
public sealed class ToshNoValue
{
    private ToshNoValue()
    {
    }

    /// <summary>The single instance; compared by reference.</summary>
    public static readonly object Instance = new ToshNoValue();

    /// <summary>Whether <paramref name="value"/> is the no-value sentinel.</summary>
    public static bool Is(object? value) => ReferenceEquals(value, Instance);

    /// <summary>The value as a reader sees it: the sentinel reads as <c>null</c>.</summary>
    public static object? Normalize(object? value) => ReferenceEquals(value, Instance) ? null : value;

    public override string ToString() => "null";
}
