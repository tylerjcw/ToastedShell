using Tosh.Language.Parsing;

namespace Tosh.Tests;

/// <summary>
/// Step 2 of the parser roadmap: a structural pre-pass that decides where
/// statements end and pipeline stages divide, without assigning meaning
/// to any token.
///
/// The value of these tests is differential. The lite pass is only worth
/// building if it agrees with the structure the existing recursive-descent
/// parser arrives at through scattered lookahead; where they agree, the
/// heuristics that produced that answer can eventually be retired. Any
/// disagreement is a finding either way.
/// </summary>
public sealed class LiteParserTests
{
    private static LiteScript Lite(string source) =>
        LiteParser.Parse(new ToshLexer(source).Lex(), source);

    /// <summary>Statement count the real parser settled on.</summary>
    private static int ParsedStatementCount(string source)
    {
        var result = ToshParser.Parse(source, "<lite-test>");
        Assert.Empty(result.Diagnostics);
        return result.Statement is ScriptStatementSyntax script
            ? script.Statements.Count
            : 1;
    }

    [Theory]
    [InlineData("echo one", 1)]
    [InlineData("echo one\necho two", 2)]
    [InlineData("echo one; echo two", 2)]
    [InlineData("echo one\n\necho two\necho three", 3)]
    [InlineData("var x = 1\nvar y = 2\necho ($x + $y)", 3)]
    public void Statement_counts_match_the_parser(string source, int expected)
    {
        Assert.Equal(expected, Lite(source).Statements.Count);
        Assert.Equal(expected, ParsedStatementCount(source));
    }

    [Theory]
    [InlineData("ls", 1)]
    [InlineData("ls | count", 2)]
    [InlineData("ls | where { _ } | sort | first 5", 4)]
    public void Stage_counts_match_the_parser(string source, int expected)
    {
        var lite = Lite(source);
        Assert.Single(lite.Statements);
        Assert.Equal(expected, lite.Statements[0].Stages.Count);

        var result = ToshParser.Parse(source, "<lite-test>");
        Assert.Empty(result.Diagnostics);
        Assert.Equal(expected, result.Pipeline.Stages.Count);
    }

    [Theory]
    // A separator inside brackets belongs to the nested construct, so it
    // must not split the enclosing statement. This is the whole reason
    // the pass tracks depth.
    [InlineData("echo (1 | count)")]
    [InlineData("[1, 2, 3] | where { _ > 1 }")]
    [InlineData("func f() {\n    echo one\n    echo two\n}")]
    [InlineData("if (true) {\n    echo yes\n} else {\n    echo no\n}")]
    public void Nested_separators_do_not_split_the_outer_statement(string source)
    {
        Assert.Single(Lite(source).Statements);
    }

    [Fact]
    public void A_block_body_stays_one_outer_statement_with_its_stages_intact()
    {
        // Two statements live inside the braces, but from the outside
        // this is a single declaration.
        var lite = Lite("func f() {\n    echo one\n    echo two\n}\nf");

        Assert.Equal(2, lite.Statements.Count);
        Assert.Single(lite.Statements[0].Stages);
        Assert.Single(lite.Statements[1].Stages);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n\n")]
    [InlineData("   ")]
    public void Empty_sources_produce_no_statements(string source)
    {
        Assert.Empty(Lite(source).Statements);
    }

    [Fact]
    public void Spans_cover_the_text_they_describe()
    {
        const string source = "echo one\necho two";
        var lite = Lite(source);

        Assert.Equal(2, lite.Statements.Count);
        Assert.Equal("echo one", source.Substring(
            lite.Statements[0].Span.Start,
            lite.Statements[0].Span.Length));
        Assert.Equal("echo two", source.Substring(
            lite.Statements[1].Span.Start,
            lite.Statements[1].Span.Length));
    }

    [Fact]
    public void Stage_spans_exclude_the_separating_pipe()
    {
        const string source = "ls | count";
        var stages = Lite(source).Statements[0].Stages;

        Assert.Equal(2, stages.Count);
        Assert.Equal("ls", source.Substring(stages[0].Span.Start, stages[0].Span.Length).Trim());
        Assert.Equal("count", source.Substring(stages[1].Span.Start, stages[1].Span.Length).Trim());
    }

    [Theory]
    // A representative sweep: whatever the construct, the lite pass and
    // the parser must agree on how many top-level statements there are.
    [InlineData("var x = 1")]
    [InlineData("func f(a, b = 2) { return $a }")]
    [InlineData("class C { prop X = 1 }")]
    [InlineData("module M { export func g() { return 1 } }")]
    [InlineData("for i in 1..3 { echo $i }")]
    [InlineData("try { echo a } catch (e) { echo b }")]
    [InlineData("match (1) {\n    1 => \"one\"\n    default => \"other\"\n}")]
    [InlineData("echo hi out> \"x.txt\"")]
    [InlineData("[1, 2] | each { _ * 2 } | collect")]
    public void Structure_agrees_with_the_parser_across_constructs(string source)
    {
        Assert.Equal(ParsedStatementCount(source), Lite(source).Statements.Count);
    }

    [Fact]
    public void A_parse_error_resynchronises_on_a_structural_boundary()
    {
        // Two declarations on one line: the second needs a separator.
        // Recovery should report that once and pick up at the next real
        // statement, rather than scanning forward token by token and
        // describing the wreckage.
        var result = ToshParser.Parse(
            "func f() { return 1 } func g() { return 2 }\nwriteline (f)",
            "<recovery-test>");

        Assert.Single(result.Diagnostics);
        Assert.Equal("tosh.parser.missing_statement_separator", result.Diagnostics[0].Code);

        // The statements after the error are still present in the tree,
        // which is what makes the result usable to the LSP.
        var script = Assert.IsType<ScriptStatementSyntax>(result.Statement);
        Assert.True(
            script.Statements.Count >= 3,
            $"expected the trailing statements to survive recovery, saw {script.Statements.Count}");
    }

    [Fact]
    public void Recovery_does_not_multiply_diagnostics_across_several_errors()
    {
        var result = ToshParser.Parse(
            "class A { prop X = 1 } class B { prop Y = 2 }\nclass C { prop Z = 3 } class D { prop W = 4 }",
            "<recovery-test>");

        // One diagnostic per genuine mistake, not a cascade.
        Assert.Equal(2, result.Diagnostics.Count);
        Assert.All(
            result.Diagnostics,
            d => Assert.Equal("tosh.parser.missing_statement_separator", d.Code));
    }

    private static IReadOnlyList<LiteBoundary> Candidates(string source) =>
        LiteParser.CandidateBoundaries(new ToshLexer(source).Lex(), source);

    [Fact]
    public void Candidates_are_found_inside_block_bodies()
    {
        // The top-level pass reports one statement here. Candidate
        // boundaries additionally expose the statements *inside* the
        // block, which is what recovery and boundary detection need.
        const string source = "func f() {\n    echo one\n    echo two\n}";

        var inBlock = Candidates(source).Where(b => b.BraceDepth > 0).ToArray();

        Assert.Single(Lite(source).Statements);
        Assert.Equal(2, inBlock.Length);
    }

    [Fact]
    public void Block_candidates_match_the_parsed_block_statement_count()
    {
        const string source = "func f() {\n    var a = 1\n    var b = 2\n    echo ($a + $b)\n}";

        var result = ToshParser.Parse(source, "<lite-test>");
        Assert.Empty(result.Diagnostics);
        var function = Assert.IsType<FunctionDefinitionStatementSyntax>(result.Statement);

        Assert.Equal(
            function.Body.Statements.Count,
            Candidates(source).Count(b => b.BraceDepth > 0));
    }

    [Fact]
    public void Grouping_suppresses_candidates_but_braces_do_not()
    {
        // A line break inside parentheses continues an expression, so it
        // is never a candidate.
        Assert.Empty(Candidates("var x = (\n    1 +\n    2\n)")
            .Where(b => b.Kind == LiteBoundaryKind.LineBreak && b.BraceDepth == 0)
            .Where(b => b.TokenIndex > 3));
    }

    [Fact]
    public void A_multi_line_record_literal_yields_no_statement_candidates()
    {
        // TS-P2-25: decidable, not guessed. `{ name = ` can only be a
        // record, because a bare `name = value` is not a legal statement —
        // assignment requires `$name`. So the line breaks inside separate
        // entries, not statements.
        const string source = "var r = {\n    a = 1\n    b = 2\n}";

        var result = ToshParser.Parse(source, "<lite-test>");
        Assert.Empty(result.Diagnostics);
        Assert.Empty(Candidates(source).Where(b => b.BraceDepth > 0));
    }

    [Theory]
    [InlineData("var r = { a = 1 }", LiteParser.BraceRole.Literal)]
    [InlineData("var r = { echo = 5 }", LiteParser.BraceRole.Literal)]
    [InlineData("var d = { \"k\" => 1 }", LiteParser.BraceRole.Literal)]
    [InlineData("func f() { echo one }", LiteParser.BraceRole.Block)]
    [InlineData("if (true) { echo yes }", LiteParser.BraceRole.Block)]
    [InlineData("[1] | each { _ * 2 }", LiteParser.BraceRole.Block)]
    public void Braces_are_classified_from_bounded_lookahead(string source, LiteParser.BraceRole expected)
    {
        var tokens = new ToshLexer(source).Lex();
        var openBrace = tokens
            .Select((token, index) => (token, index))
            .First(pair => pair.token.Kind == SyntaxTokenKind.OpenBrace)
            .index;

        Assert.Equal(expected, LiteParser.ClassifyBrace(tokens, openBrace));
    }

    [Fact]
    public void A_match_arm_list_contributes_no_statement_candidates()
    {
        // Match arms are separated by line breaks too, but they are arms,
        // not statements. Dict literals and match arms are not
        // distinguished, and need not be — neither is a block.
        const string source = "var r = match (1) {\n    1 => \"one\"\n    default => \"other\"\n}";

        var result = ToshParser.Parse(source, "<lite-test>");
        Assert.Empty(result.Diagnostics);
        Assert.Empty(Candidates(source).Where(b => b.BraceDepth > 0));
    }

    [Fact]
    public void Explicit_separators_are_reported_as_such()
    {
        var kinds = Candidates("echo one; echo two").Select(b => b.Kind).ToArray();
        Assert.Contains(LiteBoundaryKind.Explicit, kinds);
    }
}
