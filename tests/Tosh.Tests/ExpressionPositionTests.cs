using Tosh.Language.Parsing;

namespace Tosh.Tests;

/// <summary>
/// Index slots and collection literals parse an expression, not a primary —
/// <c>TS-P2-72</c>.
/// </summary>
/// <remarks>
/// <para>
/// Both positions called <c>ParseArgument</c>, which stops before a binary operator. So
/// <c>$p[$i - 1]</c> — the obvious way to reach the last element — failed with "A closing
/// ']' is required here", a message about the bracket rather than about the expression,
/// while <c>$p[($i - 1)]</c> worked. The fix was a paren the diagnostic never mentioned.
/// </para>
/// <para>
/// Found writing the ToastScript port of <c>extract_diagnostic_codes.py</c>, where
/// <c>$parts[$parts.Length - 1]</c> is how anyone takes the last segment of a dotted code;
/// the collection-literal half turned up in the grammar-generator port, which needed
/// nineteen dict values parenthesised before it would parse at all.
/// </para>
/// </remarks>
public sealed class ExpressionPositionTests
{
    private static void ParsesClean(string source)
    {
        var result = ToshParser.Parse(source, "<probe>");

        Assert.True(
            result.Diagnostics.Count == 0,
            source + "\n  " + string.Join(
                "\n  ",
                result.Diagnostics.Select(d => $"{d.Code} — {d.Title}")));
    }

    [Theory]
    [InlineData("var p = [1, 2, 3]\nvar i = 2\necho $p[$i - 1]")]
    [InlineData("var p = [1, 2, 3]\necho $p[$p.Length - 1]")]
    [InlineData("var p = [1, 2, 3]\necho $p[1 - 1]")]
    [InlineData("var p = [1, 2, 3]\necho $p[(1 + 1)]")]
    [InlineData("var d = {% \"ab\" => 9 %}\necho $d[\"a\" + \"b\"]")]
    [InlineData("var p = [1, 2, 3]\nvar i = 0\necho $p[$i > 0 ? 1 : 2]")]
    public void An_index_accepts_a_full_expression(string source) => ParsesClean(source);

    [Theory]
    [InlineData("var x = \"b\"\nvar d = {% \"k\" => \"a\" + $x %}")]
    [InlineData("var x = \"b\"\nvar l = [\"a\" + $x]")]
    [InlineData("var x = 1\nvar r = {| n = $x + 1 |}")]
    [InlineData("var n = 2\nvar l = [$n * 2, $n - 1, $n % 2]")]
    public void A_collection_value_accepts_a_full_expression(string source) => ParsesClean(source);

    // ── Nothing that already parsed changed ────────────────────────────────────

    [Theory]
    [InlineData("var p = [1, 2, 3]\necho $p[1]")]
    [InlineData("var d = {% \"k\" => 1 %}\necho $d[\"k\",]")]
    [InlineData("var d = {% \"k\" => 1 %}\necho $d[,1]")]
    [InlineData("var r = {| a = 1, b = 2 |}")]
    [InlineData("var l = [1, 2]\nvar m = [...$l, 3]")]
    [InlineData("var s = {: 1, 2 :}")]
    public void The_forms_that_already_worked_still_parse(string source) => ParsesClean(source);

    [Fact]
    public void An_empty_index_still_reports_its_own_diagnostic()
    {
        // The operator parser always returns something, so the empty slot has to be
        // detected before it runs or `$p[]` would silently index by an empty bareword.
        var result = ToshParser.Parse("var p = [1]\necho $p[]", "<probe>");

        Assert.Contains(result.Diagnostics, d => d.Code == "tosh.parser.expected_index_expression");
    }

    [Fact]
    public void A_mis_spaced_dict_closer_still_gets_its_targeted_diagnostic()
    {
        // `TS-P2-25`'s diagnostic, and the one regression this change caused: with the
        // operator parser in place, `7 % }` consumed the `%` as modulo and the helpful
        // message was replaced by `expected_operand`. `|` and `:` need no such guard
        // because neither is a binary operator here — only the dict case regressed.
        var result = ToshParser.Parse("echo {% \"key\" => 7 % }", "<probe>");

        Assert.Contains(result.Diagnostics, d => d.Code == "tosh.parser.spaced_literal_delimiter");
    }

    [Fact]
    public void Modulo_still_works_inside_a_dict_value()
    {
        // The other half of that guard: it must decline only a `%` that closes the
        // literal, not every `%` in a dict value.
        ParsesClean("var n = 7\nvar d = {% \"rem\" => $n % 2 %}");
    }
}
