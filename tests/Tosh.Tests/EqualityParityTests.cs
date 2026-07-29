using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// Drift guard for the two equality implementations,
/// <see cref="OperatorEvaluator.AreEqual"/> and <c>ToshEngine.AreEqualAsync</c>.
/// </summary>
/// <remarks>
/// <para>
/// Both exist because a user-defined <c>Equals</c> may be asynchronous, so the
/// async path cannot simply delegate. <c>TS-P1-14</c>'s acceptance says the
/// conversion matrix is "implemented once and used by every surface"; it is in
/// fact implemented twice, and the two agree only because someone maintains
/// them in step.
/// </para>
/// <para>
/// That is not hypothetical. The <c>TS-P1-14</c> equality change originally
/// landed on the evaluator alone, and <c>TS-P1-15</c> found the engine still
/// carrying the old rule. It was fixed without adding a test, so nothing
/// prevented a recurrence. This is that test — the equality counterpart of
/// <c>AnnotatedConversionParityTests</c>.
/// </para>
/// </remarks>
public sealed class EqualityParityTests
{
    /// <summary>
    /// Pairs spanning the rules `TS-P1-14` and `TS-P1-15` settled: conversion-backed
    /// numeric equality, uniform case sensitivity, enum-versus-number, collections,
    /// and nulls.
    /// </summary>
    public static IEnumerable<object?[]> Corpus()
    {
        var cases = new (object? Left, object? Right)[]
        {
            (1, 1),
            (1, 2),
            (1, "1"),          // conversion-backed equality is deliberately kept
            ("1", 1),
            (1, 1L),
            (1, 1.0),
            (1.5, 1.5),
            ("abc", "abc"),
            ("abc", "ABC"),    // case sensitivity is uniform after TS-P1-14
            ("abc", "abd"),
            (true, true),
            (true, 1),
            (null, null),
            (null, 0),
            (null, ""),
            ("", ""),
            (new object?[] { 1, 2 }, new object?[] { 1, 2 }),
            (new object?[] { 1, 2 }, new object?[] { 1, 3 }),
            (new object?[] { 1, 2 }, new object?[] { 1 }),
            (new object?[] { }, new object?[] { }),
            (new object?[] { 1, new object?[] { 2 } }, new object?[] { 1, new object?[] { 2 } }),
            ("2", 2.0),
            (0, -0),
            (double.NaN, double.NaN),
        };

        foreach (var (left, right) in cases)
        {
            yield return [left, right];
        }
    }

    [Theory]
    [MemberData(nameof(Corpus))]
    public async Task Sync_and_async_equality_agree(object? left, object? right)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var sync = OperatorEvaluator.AreEqual(left, right);
        var async = await engine.AreEqualAsync(left, right, CancellationToken.None);

        Assert.Equal(sync, async);
    }

    [Theory]
    [MemberData(nameof(Corpus))]
    public async Task Both_paths_are_symmetric(object? left, object? right)
    {
        // Asymmetry was the concrete defect behind TS-P1-14: `"abc" < 5` and
        // `5 > "abc"` disagreed. Equality has to hold the same property, and it
        // has to hold on both implementations rather than just the one a script
        // happens to reach.
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        Assert.Equal(
            OperatorEvaluator.AreEqual(left, right),
            OperatorEvaluator.AreEqual(right, left));

        Assert.Equal(
            await engine.AreEqualAsync(left, right, CancellationToken.None),
            await engine.AreEqualAsync(right, left, CancellationToken.None));
    }

    [Fact]
    public async Task Bool_against_string_is_asymmetric_today()
    {
        // CHARACTERIZATION, not a contract. `TS-P1-26` decides which direction
        // is right; when it lands this becomes an equality assertion and moves
        // into Corpus(). Pinned here rather than left out so the asymmetry stays
        // visible instead of being quietly excluded from the guard.
        //
        // Only this pair is affected: numeric-against-string and
        // bool-against-number both coerce in either direction.
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        Assert.True(OperatorEvaluator.AreEqual(true, "true"));
        Assert.False(OperatorEvaluator.AreEqual("true", true));

        // Both implementations share the asymmetry, so this is one rule applied
        // in one direction rather than a sync/async divergence.
        Assert.True(await engine.AreEqualAsync(true, "true", CancellationToken.None));
        Assert.False(await engine.AreEqualAsync("true", true, CancellationToken.None));
    }

    [Fact]
    public async Task Enum_members_compare_the_same_way_on_both_paths()
    {
        // TS-P1-15's rule, and the one that exposed the divergence in the first
        // place: a member equals its backing value in both directions.
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        await engine.ExecuteToListAsync("enum Level : int { Low = 0, Mid = 1, High = 2 }");

        var mid = Assert.Single(await engine.ExecuteToListAsync("Level.Mid"));

        foreach (var other in new object?[] { 1, 0, "Mid", "Low", mid })
        {
            Assert.Equal(
                OperatorEvaluator.AreEqual(mid, other),
                await engine.AreEqualAsync(mid, other, CancellationToken.None));
        }
    }
}
