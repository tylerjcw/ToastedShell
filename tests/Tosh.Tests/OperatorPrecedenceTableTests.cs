using System.Text.RegularExpressions;
using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// The precedence table in the specification says what the parser does — `TOAST-0003`.
/// </summary>
/// <remarks>
/// <para>
/// Four of the twelve documentation-drift boxes in `TOAST-0003` were entries in one
/// table, which is what a table maintained by hand beside a parser does. Three of them
/// were real when measured: the specification gave comparison, type testing and
/// membership as three levels when the parser has one, and it placed the ternary above
/// `??` when `??` binds tighter. The fourth — `**` against unary minus — was **not** a
/// precedence defect at all: `-2 ** 2` is `4` because `-2` lexes as a negative
/// *literal*, and `-$x ** 2` with `$x` of `2` is `-4`, which is `**` binding tighter
/// exactly as the table always said.
/// </para>
/// <para>
/// The item asked for the table to be generated from the `TS-P2-10` surface registry
/// instead of written. It cannot be: the registry records an operator's *category*
/// (`==` is `Comparison`) and no precedence at all, so generating from it would invent
/// the very thing in dispute. This guard is the alternative that needs no new data —
/// each adjacent pair of levels is pinned by an expression whose *answer differs*
/// depending on how it groups, and the table is then required to match. The parser
/// cannot drift from the tests because they run it; the table cannot drift from the
/// parser because the last test reads it.
/// </para>
/// </remarks>
public sealed class OperatorPrecedenceTableTests
{
    private static string RepositoryRoot() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));

    private static async Task<string> RunAsync(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync(source);
        return results.Count == 0 ? string.Empty : results[^1]?.ToString() ?? "null";
    }

    /// <summary>
    /// The levels, tightest first, as the specification's table gives them.
    /// </summary>
    /// <remarks>
    /// Held here as data so the LaTeX check below has something to compare against. The
    /// operator spellings are the table's, minus its LaTeX escaping.
    /// </remarks>
    private static readonly string[][] Levels =
    [
        ["as"],
        ["**"],
        ["not", "bnot", "-"],
        ["*", "/", "//", "%"],
        ["+", "-"],
        ["shl", "shr"],
        ["band"],
        ["bxor"],
        ["bor"],
        [".."],
        [
            "==", "!=", "<", ">", "<=", ">=", "=~", "!~", "is", "is not", "in",
            "not in", "is in", "is not in", "contains", "starts-with", "ends-with", "has",
        ],
        ["and", "&&"],
        ["or", "||"],
        ["??"],
        ["?", ":"],
        ["=", "+=", "-=", "*=", "**=", "/=", "//=", "%=", "??="],
    ];

    /// <summary>
    /// One expression per adjacent pair of levels, each chosen so the two groupings give
    /// **different** answers. An expression that answers the same either way pins
    /// nothing, and several obvious candidates are exactly that — `null ?? 1 ? "a" : "b"`
    /// is `a` under both readings, which is why the original attempt at this item
    /// reported the ternary and `??` as indistinguishable and stopped.
    /// </summary>
    public static TheoryData<string, string, string> AdjacentLevels() => new()
    {
        // `as` over `**`: the other grouping asks for `int ** 2` as a type name.
        { "as / **", "(\"2\" as int ** 2)", "4" },

        // `**` over unary: `-(2 ** 2)`, not `(-2) ** 2`. A variable is required —
        // with a literal the lexer folds the sign in and the question never reaches
        // the parser.
        { "** / unary", "var x = 2\n(-$x ** 2)", "-4" },

        // unary over multiplicative: `bnot 1` is `-2`, so `-2 * 2`; the other reading
        // is `bnot 2`, which is `-3`.
        { "unary / multiplicative", "(bnot 1 * 2)", "-4" },

        { "multiplicative / additive", "(2 + 3 * 4)", "14" },

        // additive over shift: `1 shl 3`, not `(1 shl 2) + 1`.
        { "additive / shift", "(1 shl 2 + 1)", "8" },

        // shift over band: `4 band 4`, not `1 shl 0`.
        { "shift / band", "(1 shl 2 band 4)", "4" },

        // band over bxor: `1 bxor 2`, not `2 band 2`.
        { "band / bxor", "(1 bxor 3 band 2)", "3" },

        // bxor over bor. Most operand triples answer the same either way here; this
        // one does not — `1 bor 0` is `1`, while `1 bxor 1` is `0`.
        { "bxor / bor", "(1 bor 1 bxor 1)", "1" },

        // bor over range: `(1 bor 2) .. 4` starts at 3. The other grouping would apply
        // `bor` to a range and fail.
        //
        // This used the left operand because `1 .. 2 bor 4` did not parse at all — the
        // right operand was read at the additive level, four levels tighter than the
        // left. `TOAST-0024` fixed that, so both sides now demonstrate the same
        // precedence and both are pinned.
        { "bor / range", "((1 bor 2 .. 4) | first)", "3" },
        { "bor / range, right operand", "((1 .. 2 bor 4) | count)", "6" },

        // range over comparison: `(1..3) == 3` is false; the other reading compares
        // `3 == 3` first and builds `1..true`.
        { "range / comparison", "(1 .. 3 == 3)", "False" },

        // comparison over `and`: `(false == false) and false` is false. The other
        // grouping is `false == (false and false)`, which is *true*.
        //
        // `1 == 1 and 2` looks like the natural case and pins nothing: `and` yields a
        // bool rather than its operand, and `1 == true` is true by conversion, so both
        // readings answer `True`.
        { "comparison / and", "(false == false and false)", "False" },

        { "and / or", "(true or false and false)", "True" },

        // `or` over `??`: `(false or null)` is false, and `false ?? \"z\"` keeps the
        // false. The other reading yields "z".
        { "or / ??", "(false or null ?? \"z\")", "False" },

        // `??` over ternary: `(\"x\" ?? \"y\")` is truthy, so the ternary answers "a".
        // Were the ternary tighter the whole expression would answer "x".
        { "?? / ternary", "(\"x\" ?? \"y\" ? \"a\" : \"b\")", "a" },

        // ternary over assignment: `$y` takes the ternary's result, not `1`.
        { "ternary / assignment", "var y = 0\n$y = 1 ? \"a\" : \"b\"\n$y", "a" },
    };

    [Theory]
    [MemberData(nameof(AdjacentLevels))]
    public async Task Each_adjacent_pair_of_levels_groups_as_the_table_says(
        string pair,
        string source,
        string expected)
    {
        var actual = await RunAsync(source);

        Assert.True(
            string.Equals(actual, expected, StringComparison.Ordinal),
            $"{pair}: `{source.Replace("\n", " ; ")}` answered `{actual}`, expected `{expected}`. " +
            "Either the parser's precedence changed or the table no longer describes it.");
    }

    /// <summary>
    /// Comparison, type testing and membership are **one** left-associative level.
    /// </summary>
    /// <remarks>
    /// The specification gave them as levels 11, 12 and 13 in that order — implying
    /// `==` binds tighter than `is` — and the parser has a single `while` loop over one
    /// predicate. `true == 1 is int` is the case that tells them apart: folded and
    /// left-associative it is `(true == 1) is int`, which is `false`; as a hierarchy
    /// with `is` tighter it is `true == (1 is int)`, which is `true`.
    /// </remarks>
    [Theory]
    [InlineData("(true == 1 is int)", "False")]
    [InlineData("(1 < 2 is bool)", "True")]
    [InlineData("(1 != 2 is bool)", "True")]
    [InlineData("(3 in [1,2,3] is bool)", "True")]
    [InlineData("(\"abc\" starts-with \"a\" is bool)", "True")]
    [InlineData("(1 <= 2 contains \"x\")", "False")]
    public async Task Comparison_type_testing_and_membership_share_one_level(
        string source,
        string expected)
        => Assert.Equal(expected, await RunAsync(source));

    /// <summary>
    /// The table in `docs/spec/toastscript-spec.tex` lists exactly the levels above, in
    /// order. This is the half that makes the document unable to drift.
    /// </summary>
    [Fact]
    public void The_specification_table_lists_these_levels_in_this_order()
    {
        var specPath = Path.Combine(RepositoryRoot(), "docs/spec/toastscript-spec.tex");
        var spec = File.ReadAllText(specPath);

        var tableStart = spec.IndexOf(@"\label{tab:precedence}", StringComparison.Ordinal);
        Assert.True(tableStart > 0, "the precedence table's label is gone from the specification");

        // Walk back to the longtable the label belongs to, then take its rows.
        var blockStart = spec.LastIndexOf(@"\begin{longtable}", tableStart, StringComparison.Ordinal);
        Assert.True(blockStart > 0, "could not find the longtable holding the precedence table");

        var block = spec[blockStart..tableStart];
        var documented = new List<string[]>();

        // The cell ends at the first *unescaped* `&`. A plain `(.*?)&` stops inside
        // `\code{\&\&}` instead, and the `and` level then reads as `[and]` with `&&`
        // silently dropped.
        foreach (Match row in Regex.Matches(
                     block,
                     @"^\s*(\d+)\s*&((?:[^&\\]|\\.)*)&",
                     RegexOptions.Multiline))
        {
            var operators = Regex.Matches(row.Groups[2].Value, @"\\code\{((?:[^{}]|\{[^{}]*\})*)\}")
                .Select(match => Unescape(match.Groups[1].Value))
                .ToArray();

            documented.Add(operators);
        }

        Assert.True(
            documented.Count == Levels.Length,
            $"the table has {documented.Count} levels, the tests pin {Levels.Length}");

        for (var index = 0; index < Levels.Length; index++)
        {
            // Set comparison: the order operators are listed *within* a level carries no
            // meaning, unlike the order of the levels themselves.
            Assert.True(
                documented[index].ToHashSet(StringComparer.Ordinal)
                    .SetEquals(Levels[index]),
                $"level {index + 1} reads [{string.Join(", ", documented[index])}] " +
                $"but is pinned as [{string.Join(", ", Levels[index])}]");
        }
    }

    /// <summary>Turns the table's LaTeX back into the operator as it is typed.</summary>
    private static string Unescape(string latex) => latex
        .Replace(@"\textasciitilde", "~", StringComparison.Ordinal)
        .Replace(@"\%", "%", StringComparison.Ordinal)
        .Replace(@"\&", "&", StringComparison.Ordinal)
        .Replace(@"\_", "_", StringComparison.Ordinal)
        .Trim();
}
