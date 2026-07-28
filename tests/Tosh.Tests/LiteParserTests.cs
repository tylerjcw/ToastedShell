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
    // the pass pairs delimiters.
    [InlineData("echo (1 | count)")]
    [InlineData("[1, 2, 3] | where { _ > 1 }")]
    [InlineData("echo {| output = ls | count |}")]
    [InlineData("echo {% \"items\" => [1, 2, 3] %}")]
    [InlineData("echo {: 1, 2, 3 :}")]
    [InlineData("func f() {\n    echo one\n    echo two\n}")]
    [InlineData("if (true) {\n    echo yes\n} else {\n    echo no\n}")]
    public void Nested_separators_do_not_split_the_outer_statement(string source)
    {
        Assert.Single(Lite(source).Statements);
    }

    [Theory]
    [InlineData("echo one\n[1, 2]")]
    [InlineData("echo one\n(1 + 2)")]
    [InlineData("echo one\n{| value = 2 |}")]
    [InlineData("echo one\n{% \"value\" => 2 %}")]
    [InlineData("echo one\n{: 2 :}")]
    public void An_opening_delimiter_can_begin_a_new_top_level_statement(string source)
    {
        Assert.Equal(2, Lite(source).Statements.Count);
        Assert.Equal(2, ParsedStatementCount(source));
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

    [Fact]
    public void Final_stage_range_excludes_the_eof_token()
    {
        const string source = "echo one";
        var tokens = new ToshLexer(source).Lex();
        var stage = Assert.Single(LiteParser.Parse(tokens, source).Statements).Stages.Single();
        var eofIndex = Assert.Single(
            tokens
                .Select((token, index) => (token, index))
                .Where(pair => pair.token.Kind == SyntaxTokenKind.EndOfFile)).index;

        Assert.Equal(eofIndex, stage.EndIndex);
        Assert.Equal(eofIndex, LiteParser.Parse(tokens, source).Statements[0].EndIndex);
        Assert.Equal(2, stage.TokenCount);
        Assert.Equal(LiteSeparatorKind.EndOfInput, stage.Separator);
    }

    [Theory]
    [InlineData("echo one | count", LiteSeparatorKind.Pipe)]
    [InlineData("echo one |> count", LiteSeparatorKind.PipeForward)]
    public void Lite_stages_record_the_separator_that_follows_them(
        string source,
        LiteSeparatorKind expected)
    {
        var stages = Lite(source).Statements[0].Stages;

        Assert.Equal(2, stages.Count);
        Assert.Equal(expected, stages[0].Separator);
        Assert.Equal(LiteSeparatorKind.EndOfInput, stages[1].Separator);
    }

    [Fact]
    public void Background_ampersand_splits_statements_but_adjacent_function_reference_does_not()
    {
        const string backgroundSource = "echo first & echo second";
        var background = Lite(backgroundSource);

        Assert.Equal(2, background.Statements.Count);
        Assert.Equal(LiteSeparatorKind.Background, background.Statements[0].Separator);
        Assert.Contains(
            Candidates(backgroundSource),
            boundary => boundary.Kind == LiteBoundaryKind.Background);

        var parsedBackground = ToshParser.Parse(backgroundSource, "<lite-consumer-test>");
        Assert.Empty(parsedBackground.Diagnostics);
        var backgroundScript = Assert.IsType<ScriptStatementSyntax>(parsedBackground.Statement);
        Assert.Collection(
            backgroundScript.Statements,
            statement => Assert.True(
                Assert.IsType<PipelineStatementSyntax>(statement).Pipeline.IsBackground),
            statement => Assert.False(
                Assert.IsType<PipelineStatementSyntax>(statement).Pipeline.IsBackground));

        const string commandReferenceSource = "echo &handler";
        var functionReference = Lite(commandReferenceSource);
        Assert.Single(functionReference.Statements);
        Assert.Equal(LiteSeparatorKind.EndOfInput, functionReference.Statements[0].Separator);

        var parsedCommandReference = ToshParser.Parse(
            commandReferenceSource,
            "<lite-consumer-test>");
        Assert.Empty(parsedCommandReference.Diagnostics);
        var echo = Assert.IsType<CommandSyntax>(
            Assert.Single(parsedCommandReference.Pipeline.Stages));
        Assert.IsType<FunctionReferenceArgumentSyntax>(Assert.Single(echo.Arguments));

        var parsedRootReference = ToshParser.Parse("&handler", "<lite-consumer-test>");
        Assert.Empty(parsedRootReference.Diagnostics);
        var rootStage = Assert.IsType<ExpressionPipelineStageSyntax>(
            Assert.Single(parsedRootReference.Pipeline.Stages));
        Assert.IsType<FunctionReferenceArgumentSyntax>(rootStage.Expression);

        Assert.Equal(2, Lite("echo & handler").Statements.Count);
        Assert.Equal(2, Lite("echo &bad-").Statements.Count);
        Assert.Equal(2, Lite("echo | &handler").Statements.Count);
    }

    [Fact]
    public void Attached_doc_comment_run_stays_with_its_declaration()
    {
        const string source =
            """
            ## Summary
            ## @returns A value.
            func f() { return 1 }
            echo after
            """;

        var lite = Lite(source);
        Assert.Equal(2, lite.Statements.Count);

        var result = ToshParser.Parse(source, "<lite-consumer-test>");
        Assert.Empty(result.Diagnostics);
        var script = Assert.IsType<ScriptStatementSyntax>(result.Statement);
        Assert.Collection(
            script.Statements,
            statement => Assert.NotNull(
                Assert.IsType<FunctionDefinitionStatementSyntax>(statement).DocComment),
            statement => Assert.IsType<PipelineStatementSyntax>(statement));
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

    [Theory]
    [InlineData(
        """
        func f()
        {
            echo one
        }
        echo after
        """)]
    [InlineData(
        """
        if (true) {
            echo yes
        }
        else {
            echo no
        }
        echo after
        """)]
    [InlineData(
        """
        try {
            echo body
        }
        catch (err) {
            echo caught
        }
        finally {
            echo cleanup
        }
        echo after
        """)]
    public void Parser_owned_top_level_continuations_are_not_split(
        string source)
    {
        var result = ToshParser.Parse(source, "<lite-consumer-test>");

        Assert.Empty(result.Diagnostics);
        var script = Assert.IsType<ScriptStatementSyntax>(result.Statement);
        Assert.Equal(2, script.Statements.Count);
        Assert.IsType<PipelineStatementSyntax>(script.Statements[1]);
    }

    [Fact]
    public void Required_operand_continuations_are_consumed_before_lite_candidates()
    {
        const string source =
            """
            var x =
                (1 + 2)
            var y = 1 +
                2
            echo done
            """;

        var result = ToshParser.Parse(source, "<lite-consumer-test>");

        Assert.Empty(result.Diagnostics);
        var script = Assert.IsType<ScriptStatementSyntax>(result.Statement);
        Assert.Collection(
            script.Statements,
            statement => Assert.Equal(
                "x",
                Assert.IsType<VariableDeclarationStatementSyntax>(statement).Name),
            statement => Assert.Equal(
                "y",
                Assert.IsType<VariableDeclarationStatementSyntax>(statement).Name),
            statement => Assert.IsType<PipelineStatementSyntax>(statement));
    }

    [Fact]
    public void Malformed_stage_stops_before_the_next_lite_statement()
    {
        const string source =
            """
            )
            echo after
            """;

        var result = ToshParser.Parse(source, "<lite-consumer-test>");

        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "tosh.parser.expected_command_name");
        var script = Assert.IsType<ScriptStatementSyntax>(result.Statement);
        Assert.Equal(2, script.Statements.Count);
        var trailing = Assert.IsType<PipelineStatementSyntax>(script.Statements[1]);
        Assert.Equal(
            "echo",
            Assert.IsType<CommandSyntax>(Assert.Single(trailing.Pipeline.Stages)).Name);
    }

    private static IReadOnlyList<LiteBoundary> Candidates(string source) =>
        LiteParser.CandidateBoundaries(new ToshLexer(source).Lex(), source);

    [Fact]
    public void Parser_proven_block_candidates_are_promoted_by_exact_owner()
    {
        // The top-level pass reports one statement here. Candidate
        // boundaries additionally expose the statements *inside* the
        // block, which is what recovery and boundary detection need.
        const string source = "func f() {\n    echo one\n    echo two\n}";

        Assert.Single(Lite(source).Statements);
        Assert.Equal(["echo", "echo"], PromotedTokenTexts(source));
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
            PromotedTokenTexts(source).Length);
    }

    [Fact]
    public void Grouping_suppresses_boundaries_but_blocks_do_not()
    {
        // A line break inside parentheses continues an expression, so it
        // is never a boundary.
        Assert.DoesNotContain(
            Candidates("var x = (\n    1 +\n    2\n)"),
            b => b.Kind == LiteBoundaryKind.LineBreak &&
                 b.BraceDepth == 0 &&
                 b.TokenIndex > 3);
    }

    [Theory]
    [InlineData("var r = {|\n    a = 1\n    b = 2\n|}")]
    [InlineData("var d = {%\n    \"a\" => 1\n    \"b\" => 2\n%}")]
    [InlineData("var s = {:\n    1,\n    2\n:}")]
    public void Multi_line_paired_literals_yield_no_statement_boundaries(string source)
    {
        var result = ToshParser.Parse(source, "<lite-test>");
        Assert.Empty(result.Diagnostics);
        Assert.DoesNotContain(Candidates(source), boundary => boundary.TokenIndex > 0);
    }

    [Theory]
    [InlineData("var r = {| a = 1; b = 2 |}")]
    [InlineData("var d = {% \"a\" => 1; \"b\" => 2 %}")]
    [InlineData("var s = {: 1; 2 :}")]
    public void Semicolons_inside_paired_literals_are_suppressed(string source)
    {
        Assert.Single(Lite(source).Statements);
        Assert.DoesNotContain(
            Candidates(source),
            boundary => boundary.Kind == LiteBoundaryKind.Explicit);
    }

    [Fact]
    public void A_literal_nested_in_a_block_suppresses_only_its_own_boundaries()
    {
        const string source =
            """
            func f() {
                var r = {|
                    a = 1
                    b = 2
                |}
                echo $r
            }
            """;

        Assert.Equal(
            ["var", "echo"],
            PromotedTokenTexts(source));
    }

    [Theory]
    [InlineData(
        "var r = {| handler = func() {\n    echo one\n    echo two\n} |}",
        "echo",
        "echo")]
    [InlineData(
        "echo (func() {\n    echo one\n    echo two\n})",
        "echo",
        "echo")]
    public void A_block_nested_in_a_suppressed_frame_reenables_its_boundaries(
        string source,
        params string[] expectedBoundaryTokens)
    {
        Assert.Equal(
            expectedBoundaryTokens,
            PromotedTokenTexts(source));
    }

    [Fact]
    public void A_group_nested_in_a_block_suppresses_only_its_own_boundaries()
    {
        const string source =
            """
            func f() {
                var x = (
                    1 +
                    2
                )
                echo $x
            }
            """;

        Assert.Equal(
            ["var", "echo"],
            PromotedTokenTexts(source));
    }

    [Theory]
    [InlineData("var {\n    Name,\n    Age\n} = $record", 0, "Name", "Age")]
    [InlineData("ps | get {\n    Name,\n    PID\n}", 0, "Name", "PID")]
    [InlineData("ps | select {\n    Name,\n    PID\n}", 0, "Name", "PID")]
    [InlineData("ps | pick {\n    Name,\n    PID\n}", 0, "Name", "PID")]
    [InlineData("require {\n    Inventory,\n    Orders\n} from \"./models.tosh\"", 0, "Inventory", "Orders")]
    [InlineData("switch (1) {\n    case 1 { echo one }\n    default { echo other }\n}", 0, "case", "default")]
    [InlineData("var r = match (1) {\n    1 => \"one\"\n    default => \"other\"\n}", 0, "1", "default")]
    [InlineData("bind LibC {\n    func first() -> int\n    func second() -> int\n}", 0, "func", "func")]
    [InlineData("interface I {\n    func first()\n    func second()\n}", 0, "func", "func")]
    [InlineData("union Result {\n    Ok(value)\n    Err(message)\n}", 0, "Ok", "Err")]
    [InlineData("enum Color {\n    Red\n    Green\n}", 0, "Red", "Green")]
    [InlineData("trait Named {\n    prop Name\n    func show()\n}", 0, "prop", "func")]
    [InlineData("event Build {\n    status = ok\n    duration = 0\n}", 0, "status", "duration")]
    [InlineData("class C {\n    prop A = 1\n    prop B = 2\n}", 0, "prop", "prop")]
    [InlineData("struct S {\n    prop A = 1\n    prop B = 2\n}", 0, "prop", "prop")]
    [InlineData("class C {\n    prop Value {\n        get => 1\n        set => 2\n    }\n}", 1, "get", "set")]
    [InlineData("type Positive = int {\n    where _ > 0\n    coerce 1\n}", 0, "where", "coerce")]
    public void Specialized_plain_braces_retain_candidates_with_exact_owner(
        string source,
        int plainBraceOrdinal,
        params string[] expectedOwnedTokens)
    {
        var tokens = new ToshLexer(source).Lex();
        var openBraces = tokens
            .Select((token, index) => (token, index))
            .Where(pair => pair.token.Kind == SyntaxTokenKind.OpenBrace)
            .Select(pair => pair.index)
            .ToArray();
        var openBraceIndex = openBraces[plainBraceOrdinal];
        var owned = LiteParser.CandidateBoundaries(tokens, source)
            .Where(boundary => boundary.OwnerOpenTokenIndex == openBraceIndex)
            .ToArray();

        Assert.Equal(
            expectedOwnedTokens,
            owned.Select(boundary => tokens[boundary.TokenIndex].Text));
        Assert.All(
            owned,
            boundary => Assert.Equal(openBraceIndex, boundary.OwnerOpenTokenIndex));
    }

    [Fact]
    public void Array_destructuring_remains_grouping_and_yields_no_internal_candidates()
    {
        const string source = "var [\n    first,\n    second\n] = $values";

        Assert.DoesNotContain(Candidates(source), boundary => boundary.TokenIndex > 0);
    }

    [Fact]
    public void Selecting_an_outer_block_does_not_promote_a_nested_specialized_brace()
    {
        const string source =
            """
            func f() {
                ps | get {
                    Name,
                    PID
                }
                echo done
            }
            """;

        var openBraces = PlainBraceOpenIndices(source);
        Assert.Equal(2, openBraces.Length);
        Assert.Equal(["ps", "echo"], PromotedTokenTexts(source, plainBraceOrdinal: 0));

        var nestedCandidates = Candidates(source)
            .Where(boundary => boundary.OwnerOpenTokenIndex == openBraces[1])
            .ToArray();
        Assert.NotEmpty(nestedCandidates);
    }

    [Fact]
    public void Match_arm_semicolons_remain_candidates_owned_by_the_match_brace()
    {
        const string source = "var r = match (1) { 1 => \"one\"; default => \"other\" }";
        var matchOpenBrace = Assert.Single(PlainBraceOpenIndices(source));

        Assert.Contains(
            Candidates(source),
            boundary => boundary.Kind == LiteBoundaryKind.Explicit &&
                        boundary.OwnerOpenTokenIndex == matchOpenBrace);
    }

    [Fact]
    public void A_block_match_arm_reenables_boundaries_inside_the_arm_body()
    {
        const string source =
            """
            var r = match (1) {
                1 => {
                    echo one
                    echo two
                }
                default => "other"
            }
            """;

        Assert.Equal(
            ["echo", "echo"],
            PromotedTokenTexts(source, plainBraceOrdinal: 1));
    }

    [Fact]
    public void A_method_block_can_be_promoted_independently_of_its_class_body()
    {
        const string source =
            """
            class C {
                func f() {
                    echo one
                    echo two
                }
                prop Value = 1
            }
            """;

        Assert.Equal(
            ["func", "prop"],
            PromotedTokenTexts(source, plainBraceOrdinal: 0));
        Assert.Equal(
            ["echo", "echo"],
            PromotedTokenTexts(source, plainBraceOrdinal: 1));
    }

    [Fact]
    public void Semicolons_inside_an_ordinary_block_are_promoted()
    {
        const string source = "func f() { echo one; echo two }";

        Assert.Equal(
            ["echo", "echo"],
            PromotedTokenTexts(source));
    }

    [Theory]
    [InlineData("echo {| a = 1 }\necho after")]
    [InlineData("func f() { echo (1 }\necho after")]
    public void Plain_brace_recovery_does_not_leak_nesting_through_eof(string source)
    {
        var tokens = new ToshLexer(source).Lex();
        var candidates = LiteParser.CandidateBoundaries(tokens, source);

        Assert.Equal(2, Lite(source).Statements.Count);
        Assert.Contains(
            candidates,
            boundary => boundary.OwnerOpenTokenIndex is null &&
                        tokens[boundary.TokenIndex].Text == "echo");
    }

    [Theory]
    [InlineData("echo {| a = 1 %}\necho after")]
    [InlineData("echo {% \"a\" => 1 :}\necho after")]
    [InlineData("echo {: 1 |}\necho after")]
    public void Mismatched_paired_closer_does_not_close_an_unrelated_literal(string source)
    {
        var tokens = new ToshLexer(source).Lex();
        var candidates = LiteParser.CandidateBoundaries(tokens, source);

        Assert.Single(Lite(source).Statements);
        Assert.DoesNotContain(
            candidates,
            boundary => boundary.OwnerOpenTokenIndex is null &&
                        tokens[boundary.TokenIndex].Text == "echo");
    }

    [Fact]
    public void Plain_brace_recovers_the_nearest_literal_without_closing_its_parent_block()
    {
        const string source =
            """
            func f() {
                echo {| value = 1 }
                echo kept
            }
            """;
        var tokens = new ToshLexer(source).Lex();
        var outerBlockOpen = PlainBraceOpenIndices(source)[0];

        var ownedEchoStarts = LiteParser
            .CandidateBoundaries(tokens, source)
            .Where(boundary => boundary.OwnerOpenTokenIndex == outerBlockOpen &&
                               tokens[boundary.TokenIndex].Text == "echo")
            .Select(boundary => tokens[boundary.TokenIndex].Span.Start)
            .ToArray();
        Assert.Equal(
            new[]
            {
                source.IndexOf("echo", StringComparison.Ordinal),
                source.LastIndexOf("echo", StringComparison.Ordinal),
            },
            ownedEchoStarts);

        var result = ToshParser.Parse(source, "<lite-recovery-test>");
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "tosh.parser.missing_record_closing_delimiter");
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "tosh.parser.missing_block_separator");

        var function = Assert.IsType<FunctionDefinitionStatementSyntax>(result.Statement);
        Assert.Collection(
            function.Body.Statements,
            statement => Assert.IsType<PipelineStatementSyntax>(statement),
            statement =>
            {
                var pipeline = Assert.IsType<PipelineStatementSyntax>(statement);
                var stage = Assert.Single(pipeline.Pipeline.Stages);
                Assert.Equal("echo", Assert.IsType<CommandSyntax>(stage).Name);
            });
    }

    [Theory]
    [InlineData("echo {| handler = func() { echo one |}\necho after")]
    [InlineData("echo ([1)\necho after")]
    public void Recovery_prefers_an_exact_closer_below_mismatched_frames(string source)
    {
        var tokens = new ToshLexer(source).Lex();
        var candidates = LiteParser.CandidateBoundaries(tokens, source);

        Assert.Equal(2, Lite(source).Statements.Count);

        var trailingBoundary = Assert.Single(
            candidates,
            boundary => boundary.OwnerOpenTokenIndex is null &&
                        tokens[boundary.TokenIndex].Text == "echo");
        Assert.Equal(0, trailingBoundary.BraceDepth);
    }

    [Fact]
    public void Pipeline_stage_continuation_is_not_promoted_as_a_block_statement()
    {
        const string source =
            """
            func f() {
                ls |
                where { _ }
                echo done
            }
            """;

        var result = ToshParser.Parse(source, "<lite-test>");
        Assert.Empty(result.Diagnostics);
        var function = Assert.IsType<FunctionDefinitionStatementSyntax>(result.Statement);

        Assert.Equal(2, function.Body.Statements.Count);

        var firstStatement = Assert.IsType<PipelineStatementSyntax>(function.Body.Statements[0]);
        Assert.Collection(
            firstStatement.Pipeline.Stages,
            stage => Assert.Equal("ls", Assert.IsType<CommandSyntax>(stage).Name),
            stage => Assert.Equal("where", Assert.IsType<CommandSyntax>(stage).Name));

        var secondStatement = Assert.IsType<PipelineStatementSyntax>(function.Body.Statements[1]);
        var echo = Assert.Single(secondStatement.Pipeline.Stages);
        Assert.Equal("echo", Assert.IsType<CommandSyntax>(echo).Name);

        Assert.Equal(["ls", "echo"], PromotedTokenTexts(source));
    }

    [Fact]
    public void Pipe_forward_stage_continuation_is_not_promoted_as_a_block_statement()
    {
        const string source =
            """
            func f() {
                echo one |>
                where { _ }
                echo done
            }
            """;

        var result = ToshParser.Parse(source, "<lite-test>");
        Assert.Empty(result.Diagnostics);
        var function = Assert.IsType<FunctionDefinitionStatementSyntax>(result.Statement);
        Assert.Equal(2, function.Body.Statements.Count);

        var firstStatement = Assert.IsType<PipelineStatementSyntax>(function.Body.Statements[0]);
        Assert.Collection(
            firstStatement.Pipeline.Stages,
            stage => Assert.Equal("echo", Assert.IsType<CommandSyntax>(stage).Name),
            stage => Assert.Equal(
                "where",
                Assert.IsType<PipeForwardStageSyntax>(stage).Command.Name));

        Assert.Equal(["echo", "echo"], PromotedTokenTexts(source));

        const string topLevelSource =
            """
            echo one |>
            where { _ }
            echo done
            """;
        var lite = Lite(topLevelSource);
        Assert.Equal(2, lite.Statements.Count);
        Assert.Equal(2, lite.Statements[0].Stages.Count);

        var topLevelResult = ToshParser.Parse(topLevelSource, "<lite-test>");
        Assert.Empty(topLevelResult.Diagnostics);
        var script = Assert.IsType<ScriptStatementSyntax>(topLevelResult.Statement);
        Assert.Equal(2, script.Statements.Count);
        Assert.Equal(
            2,
            Assert.IsType<PipelineStatementSyntax>(script.Statements[0]).Pipeline.Stages.Count);

        const string chainedSource =
            """
            echo one |> where { _ } |>
            first 1
            echo done
            """;
        var chainedLite = Lite(chainedSource);
        Assert.Equal(2, chainedLite.Statements.Count);
        Assert.Equal(3, chainedLite.Statements[0].Stages.Count);

        var chainedResult = ToshParser.Parse(chainedSource, "<lite-test>");
        Assert.Empty(chainedResult.Diagnostics);
        var chainedScript = Assert.IsType<ScriptStatementSyntax>(chainedResult.Statement);
        Assert.Equal(2, chainedScript.Statements.Count);
        var chainedPipeline =
            Assert.IsType<PipelineStatementSyntax>(chainedScript.Statements[0]).Pipeline;
        Assert.Equal(3, chainedPipeline.Stages.Count);
        Assert.Equal(
            "first",
            Assert.IsType<PipeForwardStageSyntax>(chainedPipeline.Stages[^1]).Command.Name);
    }

    [Fact]
    public void Pipe_forward_targets_share_ordinary_post_stage_handling()
    {
        const string mixedSource = "echo one |> where { _ } | count";
        var mixed = ToshParser.Parse(mixedSource, "<lite-consumer-test>");

        Assert.Empty(mixed.Diagnostics);
        Assert.Collection(
            mixed.Pipeline.Stages,
            stage => Assert.Equal("echo", Assert.IsType<CommandSyntax>(stage).Name),
            stage => Assert.Equal(
                "where",
                Assert.IsType<PipeForwardStageSyntax>(stage).Command.Name),
            stage => Assert.Equal("count", Assert.IsType<CommandSyntax>(stage).Name));

        const string redirectedSource = "echo one |> cat out> \"x\" | count";
        var redirected = ToshParser.Parse(redirectedSource, "<lite-consumer-test>");

        Assert.Empty(redirected.Diagnostics);
        Assert.Collection(
            redirected.Pipeline.Stages,
            stage => Assert.Equal("echo", Assert.IsType<CommandSyntax>(stage).Name),
            stage => Assert.Equal(
                "cat",
                Assert.IsType<PipeForwardStageSyntax>(stage).Command.Name),
            stage => Assert.Equal("count", Assert.IsType<CommandSyntax>(stage).Name));
        Assert.Single(redirected.Pipeline.Redirections!);
    }

    [Fact]
    public void Nested_pipeline_stages_remain_owned_by_the_nested_parser_region()
    {
        const string source = "echo $(ls | count) | first";
        var result = ToshParser.Parse(source, "<lite-consumer-test>");

        Assert.Empty(result.Diagnostics);
        Assert.Equal(2, result.Pipeline.Stages.Count);

        var echo = Assert.IsType<CommandSyntax>(result.Pipeline.Stages[0]);
        var substitution = Assert.IsType<CommandSubstitutionArgumentSyntax>(
            Assert.Single(echo.Arguments));
        Assert.Equal(2, substitution.Pipeline.Stages.Count);
        Assert.Equal("first", Assert.IsType<CommandSyntax>(result.Pipeline.Stages[1]).Name);
    }

    [Fact]
    public void Parser_block_statement_starts_match_exact_owner_promotions()
    {
        const string source =
            """
            func f() {
                echo one
                echo two; echo three
            }
            """;

        var tokens = new ToshLexer(source).Lex();
        var openBraceIndex = Assert.Single(PlainBraceOpenIndices(source));
        var promotedStarts = LiteParser
            .PromoteBoundariesForBlock(
                LiteParser.CandidateBoundaries(tokens, source),
                openBraceIndex)
            .Select(boundary => tokens[boundary.TokenIndex].Span.Start)
            .ToArray();

        var result = ToshParser.Parse(source, "<lite-consumer-test>");
        Assert.Empty(result.Diagnostics);
        var function = Assert.IsType<FunctionDefinitionStatementSyntax>(result.Statement);

        Assert.Equal(
            promotedStarts,
            function.Body.Statements.Select(statement => statement.Span.Start).ToArray());
    }

    [Fact]
    public void Nested_parser_blocks_consume_boundaries_from_their_own_owner()
    {
        const string source =
            """
            func outer() {
                if (true) {
                    echo inner-one
                    echo inner-two
                }
                echo outer
            }
            """;

        var result = ToshParser.Parse(source, "<lite-consumer-test>");
        Assert.Empty(result.Diagnostics);
        var function = Assert.IsType<FunctionDefinitionStatementSyntax>(result.Statement);
        Assert.Equal(2, function.Body.Statements.Count);
        Assert.Equal(
            PromotedTokenStarts(source, plainBraceOrdinal: 0),
            function.Body.Statements.Select(statement => statement.Span.Start).ToArray());

        var conditional = Assert.IsType<IfStatementSyntax>(function.Body.Statements[0]);
        Assert.Equal(2, conditional.ThenBlock.Statements.Count);
        Assert.Equal(
            PromotedTokenStarts(source, plainBraceOrdinal: 1),
            conditional.ThenBlock.Statements.Select(statement => statement.Span.Start).ToArray());
    }

    [Fact]
    public void Specialized_brace_separators_still_work_inside_a_parser_block()
    {
        const string source =
            """
            func outer() {
                class C {
                    prop A = 1
                    prop B = 2
                }
                echo done
            }
            """;

        var result = ToshParser.Parse(source, "<lite-consumer-test>");
        Assert.Empty(result.Diagnostics);
        var function = Assert.IsType<FunctionDefinitionStatementSyntax>(result.Statement);
        Assert.Equal(2, function.Body.Statements.Count);

        var definition = Assert.IsType<ClassDefinitionStatementSyntax>(function.Body.Statements[0]);
        Assert.Equal(2, definition.Members.Count);
    }

    [Fact]
    public void Same_line_block_recovery_preserves_the_next_declaration()
    {
        const string source =
            """
            func outer() {
                func a() { return 1 } func b() { return 2 }
                echo done
            }
            """;

        var result = ToshParser.Parse(source, "<lite-consumer-test>");

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("tosh.parser.missing_block_separator", diagnostic.Code);

        var function = Assert.IsType<FunctionDefinitionStatementSyntax>(result.Statement);
        Assert.Collection(
            function.Body.Statements,
            statement => Assert.Equal("a", Assert.IsType<FunctionDefinitionStatementSyntax>(statement).Name),
            statement => Assert.Equal("b", Assert.IsType<FunctionDefinitionStatementSyntax>(statement).Name),
            statement => Assert.IsType<PipelineStatementSyntax>(statement));
    }

    [Fact]
    public void Doc_comment_led_statements_are_exact_owner_boundaries()
    {
        const string source =
            """
            func outer() {
                echo one
                ## @summary Nested helper
                func nested() { return 1 }
            }
            """;

        var result = ToshParser.Parse(source, "<lite-consumer-test>");
        Assert.Empty(result.Diagnostics);
        var function = Assert.IsType<FunctionDefinitionStatementSyntax>(result.Statement);

        Assert.Collection(
            function.Body.Statements,
            statement => Assert.IsType<PipelineStatementSyntax>(statement),
            statement => Assert.Equal(
                "nested",
                Assert.IsType<FunctionDefinitionStatementSyntax>(statement).Name));

        var tokens = new ToshLexer(source).Lex();
        var boundaries = LiteParser.PromoteBoundariesForBlock(
            LiteParser.CandidateBoundaries(tokens, source),
            PlainBraceOpenIndices(source)[0]);
        Assert.Contains(
            boundaries,
            boundary => tokens[boundary.TokenIndex].Kind == SyntaxTokenKind.DocComment);
    }

    [Fact]
    public void Repeated_unmatched_closer_recovery_stress_preserves_following_structure()
    {
        const int depth = 4096;
        var source =
            "func f() {" +
            new string('(', depth) +
            new string(']', depth) +
            "}\necho after";
        var tokens = new ToshLexer(source).Lex();

        Assert.Equal(2, LiteParser.Parse(tokens, source).Statements.Count);
        Assert.Contains(
            LiteParser.CandidateBoundaries(tokens, source),
            boundary => boundary.OwnerOpenTokenIndex is null &&
                        tokens[boundary.TokenIndex].Text == "echo");
    }

    [Fact]
    public void Explicit_separators_are_reported_as_such()
    {
        var kinds = Candidates("echo one; echo two").Select(b => b.Kind).ToArray();
        Assert.Contains(LiteBoundaryKind.Explicit, kinds);
    }

    private static string[] PromotedTokenTexts(
        string source,
        int plainBraceOrdinal = 0)
    {
        var tokens = new ToshLexer(source).Lex();
        var openBraces = tokens
            .Select((token, index) => (token, index))
            .Where(pair => pair.token.Kind == SyntaxTokenKind.OpenBrace)
            .Select(pair => pair.index)
            .ToArray();
        var openBraceIndex = openBraces[plainBraceOrdinal];
        var candidates = LiteParser.CandidateBoundaries(tokens, source);

        return LiteParser.PromoteBoundariesForBlock(candidates, openBraceIndex)
            .Select(boundary => tokens[boundary.TokenIndex].Text)
            .ToArray();
    }

    private static int[] PromotedTokenStarts(
        string source,
        int plainBraceOrdinal = 0)
    {
        var tokens = new ToshLexer(source).Lex();
        var openBraceIndex = PlainBraceOpenIndices(source)[plainBraceOrdinal];

        return LiteParser
            .PromoteBoundariesForBlock(
                LiteParser.CandidateBoundaries(tokens, source),
                openBraceIndex)
            .Select(boundary => tokens[boundary.TokenIndex].Span.Start)
            .ToArray();
    }

    private static int[] PlainBraceOpenIndices(string source) =>
        new ToshLexer(source).Lex()
            .Select((token, index) => (token, index))
            .Where(pair => pair.token.Kind == SyntaxTokenKind.OpenBrace)
            .Select(pair => pair.index)
            .ToArray();
}
