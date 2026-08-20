using System.Globalization;
using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// Ordering, as the specification's §Ordering states it — `TOAST-0018`.
/// </summary>
/// <remarks>
/// <para>
/// The prose was written first and this corpus from it, which is the discipline the item
/// asks for. Phase A of `SELF_HOSTING_RFC.md` names ordering as one of ten concerns to
/// specify; it had **three** implementations and no specification.
/// </para>
/// <para>
/// The three were `OperatorEvaluator.EvaluateOrderedComparison` for `&lt;`, `SortCommand`'s
/// `ShellSortComparer`, and a simplified copy of the latter in `ToshEngine` for the fused
/// `sort | first` path. There is now one comparer in the runtime, and the operators differ
/// from it only where they are documented to: an operator may refuse a pair with no order,
/// a sort may not.
/// </para>
/// </remarks>
public sealed class ValueOrderingTests
{
    private static async Task<string> RunAsync(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync(source);
        return results.Count == 0 ? string.Empty : results[^1]?.ToString() ?? "null";
    }

    private static bool LessThan(object? left, object? right) =>
        OperatorEvaluator.EvaluateOrderedComparison(left, right, nullable: true, c => c < 0);

    /// <summary>
    /// Strings order by code point, so `"a" &lt; "B"` is false.
    /// </summary>
    [Theory]
    [InlineData("(\"abc\" < \"def\")", "True")]
    [InlineData("(\"a\" < \"B\")", "False")]     // 'B' is 66, 'a' is 97
    [InlineData("(\"B\" < \"a\")", "True")]
    [InlineData("(\"a\" < \"A\")", "False")]
    [InlineData("(\"A\" < \"a\")", "True")]
    public async Task Strings_order_by_code_point(string source, string expected)
        => Assert.Equal(expected, await RunAsync(source));

    /// <summary>
    /// The same answer on every machine. This is the whole reason for code point.
    /// </summary>
    /// <remarks>
    /// Culture collation put the meaning of a program in the hands of the machine running
    /// it: <c>"z" &lt; "ä"</c> is false under an American collation and true under a
    /// Swedish one. .NET carries ICU's data, so the locale need not even be installed for
    /// the answer to differ — which is why this test can create the culture directly.
    ///
    /// Driven through <c>OperatorEvaluator</c> rather than the engine on purpose: culture
    /// is thread-state, and an async engine call may resume on another thread and leave the
    /// assertion measuring nothing.
    /// </remarks>
    [Theory]
    [InlineData("en-US")]
    [InlineData("sv-SE")]
    [InlineData("tr-TR")]
    [InlineData("")]        // invariant
    public void Ordering_does_not_change_with_the_ambient_culture(string cultureName)
    {
        var original = CultureInfo.CurrentCulture;

        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(cultureName);
            var named = cultureName.Length == 0 ? "the invariant culture" : cultureName;

            Assert.True(LessThan("z", "ä"), $"`\"z\" < \"ä\"` must hold under {named}");
            Assert.False(LessThan("a", "B"), $"`\"a\" < \"B\"` must not hold under {named}");
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    /// <summary>
    /// Ordering agrees with equality: values `==` calls different are strictly ordered.
    /// </summary>
    /// <remarks>
    /// The property that decided the string rule. A case-insensitive order reports `"a"`
    /// and `"A"` as neither less, nor greater — while `==` reports them unequal, which is
    /// a value with no place in the order at all.
    /// </remarks>
    [Theory]
    [InlineData("a", "A")]
    [InlineData("abc", "ABC")]
    [InlineData("apple", "Banana")]
    public void Trichotomy_holds_against_equality(string left, string right)
    {
        Assert.False(OperatorEvaluator.AreEqual(left, right), "the fixture wants two unequal strings");

        var less = LessThan(left, right);
        var greater = LessThan(right, left);

        Assert.True(less ^ greater, $"`{left}` and `{right}` are unequal, so exactly one ordering must hold");
    }

    /// <summary>Numbers order across widths, and a StorageSize orders by bytes.</summary>
    [Theory]
    [InlineData("(1 < 2.5)", "True")]
    [InlineData("(2.5 < 1)", "False")]
    [InlineData("(1kb < 2mb)", "True")]
    [InlineData("(1kb < 2000)", "True")]
    [InlineData("(2000 < 1kb)", "False")]
    public async Task Numbers_order_across_widths(string source, string expected)
        => Assert.Equal(expected, await RunAsync(source));

    /// <summary>An enum member orders by its backing value.</summary>
    [Theory]
    [InlineData("enum E { A, B }\n(E.A < E.B)", "True")]
    [InlineData("enum E { A, B }\n(E.A < 1)", "True")]
    [InlineData("enum E { A, B }\n(E.B < 1)", "False")]
    public async Task An_enum_member_orders_by_its_backing_value(string source, string expected)
        => Assert.Equal(expected, await RunAsync(source));

    /// <summary>
    /// `null` is outside the order, not below it.
    /// </summary>
    /// <remarks>
    /// Every direction is false, `null &lt; null` included. The consequence is the part
    /// worth pinning: `!(x &lt; y)` does not imply `x &gt;= y` when either side is null,
    /// so the usual negation shortcut is unsound here.
    /// </remarks>
    [Theory]
    [InlineData("(null < 1)", "False")]
    [InlineData("(1 < null)", "False")]
    [InlineData("(null < null)", "False")]
    [InlineData("(null >= 1)", "False")]
    [InlineData("(1 >= null)", "False")]
    public async Task Null_is_unordered_in_every_direction(string source, string expected)
        => Assert.Equal(expected, await RunAsync(source));

    /// <summary>
    /// Pairs with no order raise, rather than inventing one.
    /// </summary>
    [Theory]
    [InlineData("(true < false)")]
    [InlineData("(false < true)")]
    [InlineData("(\"10\" < 9)")]
    [InlineData("(9 < \"10\")")]
    [InlineData("enum E { A }\nenum F { A }\n(E.A < F.A)")]
    public async Task A_pair_with_no_order_raises(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        await Assert.ThrowsAnyAsync<Exception>(() => engine.ExecuteToListAsync(source));
    }

    /// <summary>
    /// `sort` and the fused `sort | first` cannot disagree, because they are one comparer.
    /// </summary>
    /// <remarks>
    /// They did. `[1, "a", 2.5] | sort` answered `1, 2.5, "a"` while `| sort | first 3`
    /// answered `2.5, 1, "a"`: the fused copy compared only values of an identical type and
    /// otherwise ordered by type *name*, putting `Double` before `Int32`, while the real
    /// comparer converts and orders numerically. A mixed-type collection is the shape that
    /// tells them apart, and neither corpus had one.
    /// </remarks>
    [Theory]
    [InlineData("[1, \"a\", 2.5]")]
    [InlineData("[3, 1, 2]")]
    [InlineData("[\"b\", \"A\", \"a\", \"B\"]")]
    [InlineData("[2mb, 1kb, 1gb]")]
    public async Task The_fused_and_unfused_sorts_agree(string literal)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var sorted = await engine.ExecuteToListAsync($"{literal} | sort");
        var fused = await engine.ExecuteToListAsync($"{literal} | sort | first {sorted.Count}");

        Assert.Equal(sorted, fused);
    }
}
