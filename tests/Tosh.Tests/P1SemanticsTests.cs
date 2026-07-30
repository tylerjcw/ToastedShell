using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// Three P1 semantic repairs landed together: record equality ignoring field order
/// (<c>TS-P1-10</c>), <c>_</c> discarding in destructuring (<c>TS-P1-11</c>), and one
/// division-by-zero rule per numeric family (<c>TS-P1-16</c>).
/// </summary>
/// <remarks>
/// Grouped because each is small and they share a shape worth stating once: in all three
/// the language had two answers for one question. Record equality answered differently
/// depending on field order; <c>_</c> both discarded and bound; division by zero answered
/// differently depending on whether the operands were literals the folder could see.
/// </remarks>
public sealed class P1SemanticsTests
{
    private static async Task<object?> EvaluateAsync(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync(source);
        return results.Count == 0 ? null : results[^1];
    }

    // ── TS-P1-10: record equality is order-independent ─────────────────────────

    [Theory]
    [InlineData("{| a = 1, b = 2 |} == {| b = 2, a = 1 |}", true)]
    [InlineData("{| a = 1, b = 2 |} == {| a = 1, b = 2 |}", true)]
    [InlineData("{| a = 1, b = 2, c = 3 |} == {| c = 3, a = 1, b = 2 |}", true)]
    // Same names, different value: unequal.
    [InlineData("{| a = 1 |} == {| a = 2 |}", false)]
    // Different field counts: unequal, regardless of the overlap.
    [InlineData("{| a = 1 |} == {| a = 1, b = 2 |}", false)]
    // Nested records compare by name at every level.
    [InlineData("{| a = {| n = 1 |} |} == {| a = {| n = 1 |} |}", true)]
    [InlineData("{| a = {| n = 1 |} |} == {| a = {| n = 2 |} |}", false)]
    public async Task Record_equality_ignores_field_order(string expression, bool expected)
    {
        Assert.Equal(expected, await EvaluateAsync(expression));
    }

    [Theory]
    // Arrays must stay order-*sensitive*. Making records order-blind must not have
    // touched sequences, and both are IEnumerable, which is exactly how records came to
    // be compared as ordered sequences in the first place.
    [InlineData("[1, 2] == [2, 1]", false)]
    [InlineData("[1, 2] == [1, 2]", true)]
    public async Task Array_equality_stays_ordered(string expression, bool expected)
    {
        Assert.Equal(expected, await EvaluateAsync(expression));
    }

    [Fact]
    public async Task Record_equality_agrees_across_both_implementations()
    {
        // OperatorEvaluator.AreEqual and ToshEngine.AreEqualAsync are the parallel pair
        // TS-P1-24 was filed for, and the first attempt at TS-P1-10 landed only on the
        // synchronous one — `==` goes through the async path, so the defect survived a
        // change that looked complete. They now share one helper; this asserts they agree.
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var left = Assert.Single(await engine.ExecuteToListAsync("{| a = 1, b = 2 |}"));
        var right = Assert.Single(await engine.ExecuteToListAsync("{| b = 2, a = 1 |}"));

        Assert.True(OperatorEvaluator.AreEqual(left, right));
        Assert.True(await engine.AreEqualAsync(left, right, CancellationToken.None));
    }

    // ── TS-P1-11: `_` discards ─────────────────────────────────────────────────

    [Fact]
    public async Task Underscore_in_array_destructuring_discards()
    {
        // `_` is also the current pipeline item, so binding it here did not merely leak a
        // name — a destructuring inside a predicate silently changed what `_` meant.
        Assert.Equal("preexisting", await EvaluateAsync(
            """
            var _ = "preexisting"
            var [a, _, c] = [1, 2, 3]
            $_
            """));
    }

    [Fact]
    public async Task Underscore_in_record_destructuring_discards()
    {
        Assert.Equal("preexisting", await EvaluateAsync(
            """
            var _ = "preexisting"
            var person = {| name = "Alice", age = 30 |}
            var { name, _ } = $person
            $_
            """));
    }

    [Fact]
    public async Task Repeated_underscores_discard_and_the_named_target_still_binds()
    {
        // The specification's own example — "Skip elements with _" — which promised this
        // behaviour before the runtime delivered it.
        Assert.Equal(30, await EvaluateAsync(
            """
            var items = [10, 20, 30, 40, 50]
            var [_, _, third] = $items
            $third
            """));
    }

    [Fact]
    public async Task A_name_merely_starting_with_underscore_still_binds()
    {
        // Only bare `_` discards. `_x` is an ordinary identifier, as in every language
        // with this convention.
        Assert.Equal(1, await EvaluateAsync(
            """
            var [_x, _y] = [1, 2]
            $_x
            """));
    }

    // ── TS-P1-16: one division-by-zero rule per numeric family ─────────────────

    [Theory]
    // Floating: IEEE 754, matching C#. The zero's declared type is irrelevant, and so is
    // whether the operands were literals the constant folder could see.
    [InlineData("var a = 10.0\nvar b = 0.0\n$a / $b", double.PositiveInfinity)]
    [InlineData("var a = 10.0\nvar b = 0\n$a / $b", double.PositiveInfinity)]
    [InlineData("var a = -10.0\nvar b = 0.0\n$a / $b", double.NegativeInfinity)]
    [InlineData("10.0 / 0.0", double.PositiveInfinity)]
    [InlineData("10.0 / 0", double.PositiveInfinity)]
    public async Task Floating_division_by_zero_is_infinite(string source, double expected)
    {
        Assert.Equal(expected, Assert.IsType<double>(await EvaluateAsync(source)));
    }

    [Theory]
    [InlineData("var a = 0.0\n$a / $a")]
    [InlineData("var a = 10.0\nvar b = 0.0\n$a % $b")]
    public async Task Floating_zero_over_zero_and_modulo_are_nan(string source)
    {
        Assert.True(double.IsNaN(Assert.IsType<double>(await EvaluateAsync(source))));
    }

    [Theory]
    // Integral and decimal division by zero remains an error, matching C#.
    [InlineData("var a = 10\nvar b = 0\n$a / $b")]
    [InlineData("var a = 10\nvar b = 0\n$a % $b")]
    public async Task Integral_division_by_zero_still_throws(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        await Assert.ThrowsAnyAsync<Exception>(
            async () => await engine.ExecuteToListAsync(source));
    }

    [Fact]
    public async Task The_folded_and_evaluated_paths_agree()
    {
        // The actual defect: `10.0 / 0.0` written as literals was constant-folded to
        // Infinity while the same doubles held in variables threw. The item was filed as
        // "depends on the zero operand's type"; the real split was folded versus
        // evaluated — two implementations of one operation.
        var folded = await EvaluateAsync("10.0 / 0.0");
        var evaluated = await EvaluateAsync("var a = 10.0\nvar b = 0.0\n$a / $b");

        Assert.Equal(folded, evaluated);
    }
}
