using Tosh.Language.Parsing;

namespace Tosh.Tests;

/// <summary>
/// A declaration with no initializer is rejected instead of eating the next line —
/// <c>TS-P1-41</c>.
/// </summary>
/// <remarks>
/// <para>
/// `var x =` with nothing after the `=` parsed clean and then consumed the **following line** as
/// its initializer. `var x =` followed by `echo "hi"` produced no output at all: the echo became
/// the initializer expression rather than a statement. Inside a function body, a following
/// `var y =` was swallowed the same way, so `y` was never declared and the binder then reported
/// that `var` looked like an unknown command — a diagnostic pointing two lines away from the
/// actual mistake.
/// </para>
/// <para>
/// Reported from the shell after the same shape turned up while checking whether the MCP server's
/// diagnostics were current. A statement that silently disappears is worse than one that fails,
/// which is why this was refiled from P2 to P1: the failure mode is losing code, not a papercut.
/// </para>
/// <para>
/// The rule is deliberately narrow. An initializer <em>may</em> continue onto the next line —
/// `var x =` followed by an indented `(1 + 2)` is a required-operand continuation, and
/// <c>LiteParserTests.Required_operand_continuations_are_consumed_before_lite_candidates</c>
/// asserts it. A first attempt at this fix rejected every line break after `=` and broke that
/// test, which is exactly what it exists for: a survey of every `.tosh` file on the machine had
/// found no uses of the style, and the survey was the wrong evidence — the behaviour is intended
/// and tested, not merely unused.
/// </para>
/// <para>
/// So only two shapes are rejected, both of which can never be a continuation: end of input, and
/// a following line that can only be another declaration. `var x = echo "hi"` across a line
/// break stays legal, because it is legal on one line and the continuation rule should not care
/// where the newline falls.
/// </para>
/// </remarks>
public sealed class DeclarationInitializerTests
{
    private static IReadOnlyList<SyntaxDiagnostic> DiagnosticsFor(string source) =>
        ToshParser.Parse(source, "<declaration-test>").Diagnostics;

    [Theory]
    // Nothing follows at all — the reported REPL case, which used to bind null in silence.
    [InlineData("var x =")]
    [InlineData("const c =")]
    // With a type annotation, which reaches the `=` through a different branch.
    [InlineData("var x: int =")]
    // A following declaration, which can never be an initializer. This is the reported function
    // case: `var y =` was eaten by `var x =`, so `y` vanished and the binder complained that
    // `var` looked like an unknown command — two lines from the actual mistake.
    [InlineData("var x =\nvar y = 1")]
    [InlineData("func f(z) {\n    var x =\n    var y =\n    echo $z\n}")]
    public void A_declaration_without_an_initializer_is_rejected(string source)
    {
        Assert.Contains(
            DiagnosticsFor(source),
            diagnostic => diagnostic.Code == "tosh.parser.expected_initializer");
    }

    [Fact]
    public void A_swallowed_declaration_is_reported_at_the_equals_sign()
    {
        // The heart of it. Before, `var y = 1` was consumed as x's initializer and the only
        // complaint came from the binder, about `var` being an unknown command. The diagnostic
        // must land on the `=` that is missing its value.
        var diagnostics = DiagnosticsFor("var x =\nvar y = 1");
        var initializer = Assert.Single(
            diagnostics.Where(diagnostic => diagnostic.Code == "tosh.parser.expected_initializer"));

        Assert.Contains("'x'", initializer.Title, StringComparison.Ordinal);
        Assert.True(
            initializer.Span.Start < "var x =".Length + 1,
            "the diagnostic should point at the '=', not at the declaration that followed it");
    }

    [Theory]
    // Ordinary declarations must be untouched — this runs on every `var` in every script.
    [InlineData("var a = 1")]
    [InlineData("var b = (2 + 3)")]
    [InlineData("var c: int = 42")]
    [InlineData("const d = \"text\"")]
    [InlineData("var e = [1, 2, 3]")]
    [InlineData("var f = {| a = 1 |}")]
    // A pipeline initializer spanning lines after it has started.
    [InlineData("var g = [1, 2, 3]\n    | count")]
    // The required-operand continuations, which are the reason the rule is narrow rather than
    // "no line break after `=`". Both are asserted by LiteParserTests as well; repeated here so
    // that a future tightening of this check fails in the file that owns the check.
    [InlineData("var x =\n    (1 + 2)")]
    [InlineData("var y = 1 +\n    2")]
    // A command as an initializer across a line break: legal on one line, so legal here.
    [InlineData("var z =\n    echo \"hi\"")]
    // Declaration with no `=` at all, which was already legal and stays legal.
    [InlineData("var h")]
    public void A_well_formed_declaration_still_parses(string source)
    {
        Assert.DoesNotContain(
            DiagnosticsFor(source),
            diagnostic => diagnostic.Code == "tosh.parser.expected_initializer");
    }
}
