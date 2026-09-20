using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// A call whose body produced no values contributes no items — <c>TOAST-0123</c>.
/// </summary>
/// <remarks>
/// <para>
/// A free function reaches a pipeline as a command and streams, so a body producing nothing
/// is an empty stream. A class method returns one <c>InvocationResult</c>, and <c>object?</c>
/// cannot say "no values" — so zero values became <see langword="null"/> and the pipeline
/// carried one null item. Any method meaning "zero or more" therefore broke on the empty
/// case, which is usually the common one: <c>ToastLib.Sdl</c>'s <c>Events.Drain</c> failed
/// on every idle frame, and reported it at the <c>where</c> that consumed it rather than at
/// the method that yielded nothing.
/// </para>
/// <para>
/// The value position is deliberately unchanged. <c>var x = ($e.Loop(0))</c> still binds
/// null, because that is what it has always bound and call sites rely on it; only a
/// pipeline, which asked for items, is told there were none.
/// </para>
/// </remarks>
public sealed class EmptyCallResultTests
{
    /// <summary>How many values the script produced.</summary>
    private static async Task<int> CountAsync(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);
        var results = await engine.ExecuteToListAsync(source);
        return results.Count;
    }

    private static async Task<string> LastAsync(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);
        var results = await engine.ExecuteToListAsync(source);
        return results.Count == 0 ? "<none>" : results[^1]?.ToString() ?? "null";
    }

    private const string WhileMethod =
        """
        class E() {
            func L(n: int) {
                var i = 0
                while ($i < $n) { $i = ($i + 1)
                                  $i }
            }
        }
        var e = new E()
        """;

    /// <summary>The reported shape: the loop body never runs, so there is nothing to yield.</summary>
    [Fact]
    public async Task A_method_that_yields_nothing_contributes_no_items()
        => Assert.Equal(0, await CountAsync($"{WhileMethod}\n$e.L(0)"));

    /// <summary>
    /// And the non-empty case is untouched, which is what the fix must not cost.
    /// </summary>
    /// <remarks>
    /// One item, not three: a call's collection is a value rather than a sequence
    /// (`TOAST-0039`), so three yielded values arrive as one array. The zero case
    /// contributing no item at all is consistent with that through `collect`, which is
    /// where a reader sees the difference.
    /// </remarks>
    [Fact]
    public async Task A_method_that_yields_values_is_unchanged()
        => Assert.Equal(1, await CountAsync($"{WhileMethod}\n$e.L(3)"));

    /// <summary>The one item is the three values, not a stand-in for them.</summary>
    [Fact]
    public async Task The_values_survive_as_a_collection()
        => Assert.Equal("3", await LastAsync($"{WhileMethod}\n(($e.L(3) | collect) | count)"));

    /// <summary>And collecting the empty case yields nothing to count.</summary>
    [Fact]
    public async Task Collecting_an_empty_call_counts_zero()
        => Assert.Equal("0", await LastAsync($"{WhileMethod}\n(($e.L(0) | collect) | count)"));

    /// <summary>
    /// The three shapes a library reaches for when it means "maybe some", each asked for
    /// none.
    /// </summary>
    [Theory]
    [InlineData("class E() { func L(n) { if ($n > 0) { $n } } }\nvar e = new E()\n$e.L(0)")]
    [InlineData("class E() { func L(xs) { for x in $xs { $x } } }\nvar e = new E()\n$e.L([])")]
    [InlineData("class E() { func L() { } }\nvar e = new E()\n$e.L()")]
    public async Task Every_empty_shape_contributes_no_items(string source)
        => Assert.Equal(0, await CountAsync(source));

    /// <summary>The same shapes, asked for some, still produce them.</summary>
    [Theory]
    [InlineData("class E() { func L(n) { if ($n > 0) { $n } } }\nvar e = new E()\n$e.L(5)", 1)]
    [InlineData("class E() { func L(xs) { for x in $xs { $x } } }\nvar e = new E()\n$e.L([1, 2, 3])", 1)]
    public async Task A_non_empty_shape_still_produces_its_values(string source, int expected)
        => Assert.Equal(expected, await CountAsync(source));

    /// <summary>
    /// The control. A free function was already correct, because it reaches a pipeline as a
    /// command; if this ever changes, the two halves have diverged again.
    /// </summary>
    [Fact]
    public async Task A_free_function_that_yields_nothing_is_still_empty()
        => Assert.Equal(
            0,
            await CountAsync("func L(n) { var i = 0\nwhile ($i < $n) { $i = ($i + 1)\n$i } }\nL(0)"));

    /// <summary>
    /// The decision, recorded: a value position keeps binding null. Changing this would
    /// change the meaning of existing code, which is why it was left alone.
    /// </summary>
    [Fact]
    public async Task A_value_position_still_binds_null()
        => Assert.Equal("True", await LastAsync($"{WhileMethod}\nvar x = ($e.L(0))\n($x == null)"));

    /// <summary>
    /// A property that yields nothing is a property with no value, which reads correctly as
    /// null. Only the method paths were changed, and this pins that boundary.
    /// </summary>
    [Fact]
    public async Task A_property_getter_that_yields_nothing_is_still_null()
        => Assert.Equal(
            "True",
            await LastAsync(
                """
                class E() {
                    prop P { get { } }
                }
                var e = new E()
                ($e.P == null)
                """));

    /// <summary>
    /// The marker is machinery, not a value. If it ever reaches a script it would print as
    /// a type name and compare equal to nothing, so this asserts the one thing a reader
    /// could observe.
    /// </summary>
    [Fact]
    public async Task The_empty_marker_is_never_visible_to_a_script()
    {
        var text = await LastAsync($"{WhileMethod}\nvar x = ($e.L(0))\n$\"[{{$x}}]\"");

        // "[null]" — the binding is null, as it has always been. What must never appear is
        // the marker itself, which would surface as a type name a script cannot name.
        Assert.Equal("[null]", text);
        Assert.DoesNotContain("Empty", text, StringComparison.Ordinal);
    }
}
