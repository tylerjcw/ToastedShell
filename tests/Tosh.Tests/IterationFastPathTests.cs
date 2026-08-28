using System.Collections;
using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// The <c>for</c> loop's single-item fast path — <c>TS-P2-125</c>.
/// </summary>
/// <remarks>
/// <para>
/// Most iterated values are atoms — an <c>int</c> from a range, a line of text — and for
/// those <c>ExpandIterationItemsAsync</c> was an async iterator built to hand back the value
/// it was given, allocated once per iteration. The loop now asks
/// <c>IsExpandableForIteration</c> and skips the enumerable and its enumerator when the answer
/// is no: 1,617 to 1,369 bytes per iteration, measured on an empty loop body, and the saving
/// applies to every loop shape because it is beneath all of them.
/// </para>
/// <para>
/// This is a <em>shortcut to</em> the existing implementation, not a second copy of it —
/// which is the test this item lays down for its own fast paths. It holds only while
/// <c>IsExpandableForIteration</c> keeps mirroring <c>ExpandCollectionLikeValue</c>, so that
/// agreement is what the first test here pins. If the two ever disagree, a value the loop
/// treats as an atom would silently iterate once instead of expanding.
/// </para>
/// </remarks>
public sealed class IterationFastPathTests
{
    /// <summary>
    /// The predicate and the expansion must agree, because the fast path trusts the predicate
    /// to predict what the expansion would have produced.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData(42)]
    [InlineData(3.5)]
    [InlineData(true)]
    [InlineData("hi")]
    [InlineData('c')]
    public void An_unexpandable_value_expands_to_exactly_itself(object? value)
    {
        Assert.False(ShellIterationUtilities.IsExpandableForIteration(value));

        var expanded = ShellIterationUtilities.ExpandCollectionLikeValue(value).ToList();

        Assert.Single(expanded);
        Assert.Equal(value, expanded[0]);
    }

    /// <summary>
    /// And the other direction: anything the predicate calls expandable must not be answered
    /// by the fast path, so it has to actually expand.
    /// </summary>
    [Fact]
    public void An_expandable_value_is_not_answered_as_a_single_item()
    {
        object[] expandable = [new[] { 1, 2, 3 }, new List<int> { 1, 2 }, (IEnumerable)"ab".ToCharArray()];

        foreach (var value in expandable)
        {
            Assert.True(ShellIterationUtilities.IsExpandableForIteration(value));
            Assert.True(ShellIterationUtilities.ExpandCollectionLikeValue(value).Count() > 1);
        }
    }

    private static async Task<IReadOnlyList<object?>> RunAsync(string script)
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime.Language);
        return await engine.ExecuteToListAsync(script);
    }

    [Fact]
    public async Task A_range_iterates_each_value()
    {
        var results = await RunAsync("""
            for i in 1..3 { echo $i }
            """);

        Assert.Equal(["1", "2", "3"], results.Select(r => r?.ToString()));
    }

    /// <summary>
    /// A collection still expands — the fast path must not swallow it as one item.
    /// </summary>
    [Fact]
    public async Task An_array_still_expands()
    {
        var results = await RunAsync("""
            var a = [1, 2, 3]
            for x in $a { echo $x }
            """);

        Assert.Equal(["1", "2", "3"], results.Select(r => r?.ToString()));
    }

    /// <summary>
    /// A nested array binds the inner array once rather than its elements — the
    /// already-expanded case, which used to build an iterator of its own for one value.
    /// </summary>
    [Fact]
    public async Task A_nested_array_binds_the_inner_array()
    {
        var results = await RunAsync("""
            for x in [[1, 2, 3]] { echo $x.Length }
            """);

        Assert.Equal("3", Assert.Single(results)?.ToString());
    }

    /// <summary>
    /// A string is an atom, so the loop runs once over the whole string rather than per
    /// character.
    /// </summary>
    [Fact]
    public async Task A_string_iterates_once()
    {
        var results = await RunAsync("""
            for x in "hi" { echo $x }
            """);

        Assert.Equal("hi", Assert.Single(results)?.ToString());
    }

    /// <summary>
    /// Control flow leaves the loop the same way it did — the fast path restructured the inner
    /// loop into a manual enumeration, which is where a break or continue could have been lost.
    /// </summary>
    [Fact]
    public async Task Break_and_continue_still_leave_the_loop()
    {
        var stopped = await RunAsync("""
            for i in 1..5 {
                if ($i == 3) { break }
                echo $i
            }
            """);

        Assert.Equal(["1", "2"], stopped.Select(r => r?.ToString()));

        var skipped = await RunAsync("""
            for i in 1..4 {
                if ($i == 2) { continue }
                echo $i
            }
            """);

        Assert.Equal(["1", "3", "4"], skipped.Select(r => r?.ToString()));
    }

    [Fact]
    public async Task A_return_still_leaves_the_enclosing_function()
    {
        var results = await RunAsync("""
            func f() {
                for i in 1..5 {
                    if ($i == 2) { return $i }
                }
                return 0
            }
            echo (f)
            """);

        Assert.Equal("2", Assert.Single(results)?.ToString());
    }

    [Fact]
    public async Task Nested_loops_still_nest()
    {
        var results = await RunAsync("""
            for i in 1..2 {
                for j in 1..2 { echo ($i * 10 + $j) }
            }
            """);

        Assert.Equal(["11", "12", "21", "22"], results.Select(r => r?.ToString()));
    }
}
