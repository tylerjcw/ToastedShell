using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// Standing check that ordering and equality agree for every type that reaches
/// the operator evaluator.
/// </summary>
/// <remarks>
/// <para>
/// Three separate repairs have had the same shape: <c>TS-P1-15</c> found enum
/// members ordered but not equal to their backing value, <c>TS-P1-26</c> found
/// equality asymmetric for bool against string, and the specification
/// conformance corpus found quantities ordered but not equal
/// (<c>5`s &gt; 4000`ms</c> true, <c>5`s == 5000`ms</c> false).
/// </para>
/// <para>
/// The cause is structural rather than incidental: ordering and equality are
/// implemented apart, so a type taught to one is not thereby taught the other.
/// After three instances the useful move is a property rather than a fourth
/// repair — state the invariant once and let it catch the next type.
/// </para>
/// <para>
/// The invariant is trichotomy. For any pair the language agrees to order,
/// exactly one of <c>a &lt; b</c>, <c>a == b</c>, <c>a &gt; b</c> holds, and
/// <c>a &lt; b</c> agrees with <c>b &gt; a</c>. A type that satisfies ordering
/// but reports every pair unequal fails the first; one that answers equality
/// while contradicting its own ordering fails the second.
/// </para>
/// </remarks>
public sealed class OrderingEqualityAgreementTests
{
    /// <summary>
    /// Pairs built through the engine rather than constructed directly, so the
    /// values are exactly what a script produces.
    /// </summary>
    private static async Task<IReadOnlyList<(string Label, object? Left, object? Right)>> BuildCorpusAsync()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        await engine.ExecuteToListAsync("enum Level : int { Low = 0, Mid = 1, High = 2 }");

        var pairs = new (string Label, string Left, string Right)[]
        {
            // Numeric, including equal values of different CLR types.
            ("int/int equal",        "1",            "1"),
            ("int/int ordered",      "1",            "2"),
            ("int/long equal",       "1",            "1"),
            ("int/double equal",     "1",            "1.0"),
            ("int/double ordered",   "1",            "1.5"),
            ("negative",             "-3",           "-1"),
            // Strings.
            ("string equal",         "\"abc\"",      "\"abc\""),
            ("string ordered",       "\"abc\"",      "\"abd\""),
            ("string case",          "\"abc\"",      "\"ABC\""),
            // Enums — TS-P1-15's shape.
            ("enum members",         "Level.Low",    "Level.High"),
            ("enum equal",           "Level.Mid",    "Level.Mid"),
            ("enum vs backing",      "Level.Mid",    "1"),
            // Quantities — the shape the conformance corpus found.
            ("quantity same unit",   "5`s",          "4`s"),
            ("quantity cross unit",  "5`s",          "5000`ms"),
            ("quantity ordered",     "1`km",         "500`m"),
            ("length cross unit",    "1`m",          "100`cm"),
            // Storage sizes.
            ("storage equal",        "10kb",         "10kb"),
            ("storage ordered",      "5kb",          "10kb"),
            // Temporal.
            ("timespan ordered",     "1d",           "2d"),
            ("timespan equal",       "1d",           "1d"),
        };

        var built = new List<(string, object?, object?)>();
        foreach (var (label, left, right) in pairs)
        {
            var l = Assert.Single(await engine.ExecuteToListAsync(left));
            var r = Assert.Single(await engine.ExecuteToListAsync(right));
            built.Add((label, l, r));
        }

        return built;
    }

    private static bool? TryEvaluate(object? left, string op, object? right)
    {
        try
        {
            return OperatorEvaluator.EvaluateBinary(left, op, right) as bool?;
        }
        catch
        {
            // Ordering a pair the language refuses is a legitimate answer, and
            // distinct from answering it wrongly.
            return null;
        }
    }

    [Fact]
    public async Task Exactly_one_of_less_equal_greater_holds()
    {
        var failures = new List<string>();

        foreach (var (label, left, right) in await BuildCorpusAsync())
        {
            var lt = TryEvaluate(left, "<", right);
            var gt = TryEvaluate(left, ">", right);

            // A pair the language declines to order is out of scope; the property
            // is about types that *do* order.
            if (lt is null || gt is null)
            {
                continue;
            }

            var eq = OperatorEvaluator.AreEqual(left, right);
            var holding = new[] { lt.Value, eq, gt.Value }.Count(x => x);

            if (holding != 1)
            {
                failures.Add(
                    $"{label}: <={lt} =={eq} >={gt} — {holding} of three hold, expected exactly one");
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [Fact]
    public async Task Ordering_is_symmetric_with_its_mirror()
    {
        var failures = new List<string>();

        foreach (var (label, left, right) in await BuildCorpusAsync())
        {
            var lt = TryEvaluate(left, "<", right);
            var mirrored = TryEvaluate(right, ">", left);

            if (lt != mirrored)
            {
                failures.Add($"{label}: a<b={lt} but b>a={mirrored}");
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [Fact]
    public async Task Equality_agrees_across_both_implementations()
    {
        // The evaluator and the engine hold separate equality implementations
        // (TS-P1-24). Any type added to one must behave the same in the other.
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var failures = new List<string>();

        foreach (var (label, left, right) in await BuildCorpusAsync())
        {
            var sync = OperatorEvaluator.AreEqual(left, right);
            var async = await engine.AreEqualAsync(left, right, CancellationToken.None);

            if (sync != async)
            {
                failures.Add($"{label}: sync={sync} async={async}");
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }
}
