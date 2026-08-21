using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// Equality, as the specification's cascade and §Numbers, null and Instances state it —
/// `TOAST-0018`.
/// </summary>
/// <remarks>
/// <para>
/// `TOAST-0003` rewrote the cascade after finding the documented version described a
/// case-insensitive textual fallback that `TS-P1-14` had removed. This finishes the box:
/// numeric widths, `null`, class instances and the float specials, which the cascade
/// named nowhere.
/// </para>
/// <para>
/// **There are two implementations and both are exercised here.** `OperatorEvaluator.AreEqual`
/// and `ToshEngine.AreEqualAsync` are structurally parallel and have diverged twice before
/// (`TS-P1-14`, `TS-P1-15`). They diverged again while this file was being written: the
/// exact-numeric rule was added to the evaluator, the suite stayed green, and nothing about
/// `==` changed — because `==` goes through the engine's copy. `Both_paths_agree` is the
/// guard that turns that from a thing you have to remember into a thing that fails.
/// </para>
/// </remarks>
public sealed class ValueEqualityTests
{
    private static async Task<string> RunAsync(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync(source);
        return results.Count == 0 ? string.Empty : results[^1]?.ToString() ?? "null";
    }

    /// <summary>A number's width is not part of what it is.</summary>
    [Theory]
    [InlineData("(1 == 1.0)", "True")]
    [InlineData("(1 == 1.5)", "False")]
    [InlineData("((1 as long) == (1 as int))", "True")]
    [InlineData("((1.5 as float) == (1.5 as double))", "True")]
    [InlineData("(1 == (1 as decimal))", "True")]
    public async Task Numbers_compare_by_value_across_widths(string source, string expected)
        => Assert.Equal(expected, await RunAsync(source));

    /// <summary>
    /// An integer against a floating value is exact, so equality stays transitive.
    /// </summary>
    /// <remarks>
    /// `2 ** 53 + 1` has no exact `double`. Deciding this pair by converting the integer
    /// made `$a == $b` and `$c == $b` both true while `$a == $c` was false — a relation
    /// that cannot back a dictionary, a `distinct`, or a cache. `$a == $b` was `true`
    /// until 2026-08-20.
    /// </remarks>
    [Fact]
    public async Task Equality_is_transitive_across_integer_and_float()
    {
        const string Setup = """
            var a = 9007199254740993 as long
            var c = 9007199254740992 as long
            var b = ($c as double)

            """;

        Assert.Equal("False", await RunAsync(Setup + "($a == $b)"));
        Assert.Equal("True", await RunAsync(Setup + "($c == $b)"));
        Assert.Equal("False", await RunAsync(Setup + "($a == $c)"));
    }

    /// <summary>The exact rule, from either side and against the values it must refuse.</summary>
    [Theory]
    [InlineData("(9007199254740993 as long) == (9007199254740992 as double)", false)]
    [InlineData("(9007199254740992 as long) == (9007199254740992 as double)", true)]
    [InlineData("1 == 1.0", true)]
    [InlineData("1 == 1.0000000000000002", false)]
    public void An_integer_equals_a_float_only_when_it_is_the_same_number(string label, bool expected)
    {
        // Built as CLR values rather than parsed, because a literal above 2^53 is a
        // different question — the lexer's — and this one is the comparison's.
        var pairs = new Dictionary<string, (object Left, object Right)>
        {
            ["(9007199254740993 as long) == (9007199254740992 as double)"] = (9007199254740993L, 9007199254740992.0),
            ["(9007199254740992 as long) == (9007199254740992 as double)"] = (9007199254740992L, 9007199254740992.0),
            ["1 == 1.0"] = (1, 1.0),
            ["1 == 1.0000000000000002"] = (1, 1.0000000000000002),
        };

        var (left, right) = pairs[label];

        Assert.Equal(expected, OperatorEvaluator.AreEqual(left, right));
        Assert.Equal(expected, OperatorEvaluator.AreEqual(right, left));
    }

    /// <summary>
    /// `NaN` equals itself, which is deliberately not IEEE 754's rule for `==`.
    /// </summary>
    /// <remarks>
    /// Equality is the relation collections are built on and has to be reflexive: under
    /// the IEEE rule a `NaN` put in a dictionary could never be found again. Signed zeroes
    /// go the other way and follow IEEE.
    /// </remarks>
    [Theory]
    [InlineData("var n = 0.0 / 0.0\n($n == $n)", "True")]
    [InlineData("var n = 0.0 / 0.0\n(1 == $n)", "False")]
    [InlineData("var n = 0.0 / 0.0\n($n == 1)", "False")]
    [InlineData("var i = 1.0 / 0.0\n(1 == $i)", "False")]
    [InlineData("(0.0 == -0.0)", "True")]
    public async Task Float_specials_follow_a_stated_rule(string source, string expected)
        => Assert.Equal(expected, await RunAsync(source));

    /// <summary>`null` equals only `null`.</summary>
    [Theory]
    [InlineData("(null == null)", "True")]
    [InlineData("(null == 0)", "False")]
    [InlineData("(null == \"\")", "False")]
    [InlineData("(null == false)", "False")]
    public async Task Null_equals_only_null(string source, string expected)
        => Assert.Equal(expected, await RunAsync(source));

    /// <summary>
    /// A class instance is equal only to itself, unless the class declares otherwise.
    /// </summary>
    /// <remarks>
    /// The opposite default from a record, and deliberately: a record is a bag of values,
    /// while a class has identity — two accounts holding the same balance are not the
    /// same account.
    /// </remarks>
    [Theory]
    [InlineData("class P(x: int) { prop X: int = $x }\n((new P(1)) == (new P(1)))", "False")]
    [InlineData("class P(x: int) { prop X: int = $x }\nvar p = new P(1)\n($p == $p)", "True")]
    [InlineData("class Q(x: int) { prop X: int = $x\n func equals(other) -> bool => ($this.X == $other.X) }\n((new Q(1)) == (new Q(1)))", "True")]
    [InlineData("class Q(x: int) { prop X: int = $x\n func equals(other) -> bool => ($this.X == $other.X) }\n((new Q(1)) == (new Q(2)))", "False")]
    public async Task A_class_instance_is_equal_only_to_itself_unless_it_says_otherwise(
        string source,
        string expected)
        => Assert.Equal(expected, await RunAsync(source));

    /// <summary>Records and containers compare by content.</summary>
    [Theory]
    [InlineData("({| a = 1, b = 2 |} == {| b = 2, a = 1 |})", "True")]
    [InlineData("([[1,2],[3]] == [[1,2],[3]])", "True")]
    [InlineData("([1,2] == [1,2,3])", "False")]
    [InlineData("({% \"a\" => 1 %} == {% \"a\" => 1 %})", "True")]
    [InlineData("({% \"a\" => 1 %} == {% \"a\" => 2 %})", "False")]
    public async Task Records_and_containers_compare_by_content(string source, string expected)
        => Assert.Equal(expected, await RunAsync(source));

    /// <summary>
    /// The two equality implementations answer the same question the same way.
    /// </summary>
    /// <remarks>
    /// `OperatorEvaluator.AreEqual` serves the shared runtime dispatcher; `==` in a script
    /// reaches `ToshEngine.AreEqualAsync`. They share only `TryCompareByName` and the
    /// numeric rule, so everything else is agreement by maintenance — which has failed
    /// twice before, and failed again while this file was written.
    /// </remarks>
    [Theory]
    [InlineData("1", "1.0", true)]
    [InlineData("1", "1.5", false)]
    [InlineData("1", "\"1\"", true)]
    [InlineData("\"ABC\"", "\"abc\"", false)]
    [InlineData("null", "null", true)]
    [InlineData("null", "0", false)]
    [InlineData("0.0", "-0.0", true)]
    [InlineData("[1,2]", "[1,2]", true)]
    [InlineData("{| a = 1 |}", "{| a = 1 |}", true)]
    // The pair that tells a one-sided change apart. Without it this guard passes while
    // only one implementation carries the exact-numeric rule — which is the state the
    // first attempt at that rule actually left the tree in.
    [InlineData("9007199254740993 as long", "9007199254740992 as double", false)]
    // `TOAST-0026`. A decimal against a double, which is the same rule and was added to
    // one implementation only — this row is what turns that into a failing test.
    [InlineData("1.0000000000000001", "1.0", false)]
    [InlineData("0.1 as decimal", "0.1", true)]
    [InlineData("9007199254740992 as long", "9007199254740992 as double", true)]
    public async Task Both_paths_agree(string left, string right, bool expected)
    {
        // Each operand is evaluated on its own. Building `[{left}, {right}]` and reading
        // the elements back does not work: a collection literal coerces its elements to a
        // common type, so `[1, 1.0]` hands back two `Double`s and the evaluator never
        // sees the mixed pair the operator was given.
        var leftValue = await EvaluateAsync(left);
        var rightValue = await EvaluateAsync(right);

        // The engine's path, which is what `==` in a script actually runs.
        var viaOperator = await RunAsync($"(({left}) == ({right}))");

        // The evaluator's path, over the same two values.
        var viaEvaluator = OperatorEvaluator.AreEqual(leftValue, rightValue);

        Assert.Equal(expected ? "True" : "False", viaOperator);
        Assert.Equal(expected, viaEvaluator);
    }

    private static async Task<object?> EvaluateAsync(string expression)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync($"({expression})");
        return results.Count == 0 ? null : results[^1];
    }
}
