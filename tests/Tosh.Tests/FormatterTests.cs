using Tosh.Language.Formatting;

namespace Tosh.Tests;

public sealed class FormatterTests
{
    [Fact]
    public void Var_decl_round_trips_with_canonical_spacing()
    {
        // Note: 'var x=42' (no spaces) parses as a pipeline statement —
        // the lexer treats 'x=42' as a single bareword arg to 'var'.
        // The canonical form is 'var x = 42', which parses as a real
        // VariableDeclarationStatementSyntax and round-trips cleanly.
        var result = ToshFormatter.Format("var   x   =   42");
        Assert.True(result.IsSyntacticallyValid);
        Assert.Equal("var x = 42\n", result.FormattedText);
    }

    [Fact]
    public void Function_with_inline_body_expands_to_block()
    {
        var result = ToshFormatter.Format("func   greet(n)   { echo $\"hi {$n}\" }");
        Assert.True(result.IsSyntacticallyValid);
        Assert.Equal(
            """
            func greet(n) {
                echo $"hi {$n}"
            }

            """.ReplaceLineEndings("\n"),
            result.FormattedText);
    }

    [Fact]
    public void If_else_block_is_expanded_and_indented()
    {
        var result = ToshFormatter.Format("if ($x > 10) { echo \"big\" } else { echo \"small\" }");
        Assert.True(result.IsSyntacticallyValid);
        Assert.Equal(
            """
            if ($x > 10) {
                echo "big"
            } else {
                echo "small"
            }

            """.ReplaceLineEndings("\n"),
            result.FormattedText);
    }

    [Fact]
    public void Top_level_declarations_get_blank_line_separators()
    {
        var input = """
            var x = 1
            func a() { echo "a" }
            func b() { echo "b" }
            """;
        var result = ToshFormatter.Format(input);
        Assert.True(result.IsSyntacticallyValid);
        Assert.Equal(
            """
            var x = 1

            func a() {
                echo "a"
            }

            func b() {
                echo "b"
            }

            """.ReplaceLineEndings("\n"),
            result.FormattedText);
    }

    [Fact]
    public void Class_declaration_preserves_closing_brace()
    {
        // Reproduces the parser-span quirk where ClassDefinitionStatementSyntax.Span
        // ends at the last member instead of the closing '}'. The formatter must
        // extend the slice to include the matching brace.
        var input = """
            class Point(x, y) {
                prop X = x
                prop Y = y
            }
            """;
        var result = ToshFormatter.Format(input);
        Assert.True(result.IsSyntacticallyValid);
        Assert.EndsWith("}\n", result.FormattedText);
        Assert.Contains("class Point(x, y) {", result.FormattedText);
        Assert.Contains("prop Y = y", result.FormattedText);
    }

    [Fact]
    public void Format_is_idempotent()
    {
        var input = """
            var x=42
            func   greet(n)   { echo $"hi {$n}" }
            if ($x>10) { echo "big" } else { echo "small" }
            for i in 1..5 { echo $i }
            func dbl(x: int) -> int => ($x * 2)
            """;
        var first = ToshFormatter.Format(input);
        var second = ToshFormatter.Format(first.FormattedText);
        Assert.Equal(first.FormattedText, second.FormattedText);
    }

    [Fact]
    public void Parse_errors_return_original_text_unchanged()
    {
        // 'if' without parens is a parse error.
        var input = "if $x > 10 { echo bad }";
        var result = ToshFormatter.Format(input);
        Assert.False(result.IsSyntacticallyValid);
        Assert.Equal(input, result.FormattedText);
        Assert.NotEmpty(result.ParseDiagnostics);
    }

    [Fact]
    public void Trailing_newline_is_normalised()
    {
        var trailingNewlines = ToshFormatter.Format("var x = 1\n\n\n\n");
        Assert.Equal("var x = 1\n", trailingNewlines.FormattedText);

        var noNewline = ToshFormatter.Format("var x = 1");
        Assert.Equal("var x = 1\n", noNewline.FormattedText);
    }

    [Fact]
    public void Postfix_conditional_falls_back_to_source_slice()
    {
        // Postfix conditionals are parsed as IfStatementSyntax wrapping the
        // inner statement; the inner span includes the postfix tail. The
        // formatter currently falls back to slice for these inline forms,
        // which is correct as long as it round-trips.
        var input = """
            func test(x) {
                return $x if ($x > 5)
                echo "small"
            }
            """;
        var result = ToshFormatter.Format(input);
        Assert.True(result.IsSyntacticallyValid);
        // Idempotency — second pass produces same text.
        var second = ToshFormatter.Format(result.FormattedText);
        Assert.Equal(result.FormattedText, second.FormattedText);
    }

    [Fact]
    public void Try_catch_finally_is_expanded_to_blocks()
    {
        var result = ToshFormatter.Format("try { risky } catch (e) { echo $e } finally { cleanup }");
        Assert.True(result.IsSyntacticallyValid);
        // Idempotency check — the exact layout of the catch/finally
        // header is up to the formatter, so we assert structural
        // properties + idempotency rather than a fixed string.
        Assert.Contains("try {", result.FormattedText);
        Assert.Contains("catch", result.FormattedText);
        Assert.Contains("finally", result.FormattedText);
        var second = ToshFormatter.Format(result.FormattedText);
        Assert.Equal(result.FormattedText, second.FormattedText);
    }

    [Fact]
    public void Switch_statement_is_expanded()
    {
        var input = "switch ($x) { case 1 { echo one } case 2 { echo two } default { echo other } }";
        var result = ToshFormatter.Format(input);
        Assert.True(result.IsSyntacticallyValid);
        var second = ToshFormatter.Format(result.FormattedText);
        Assert.Equal(result.FormattedText, second.FormattedText);
    }

    [Fact]
    public void Variable_assignment_round_trips()
    {
        var result = ToshFormatter.Format("var x = 1\n$x   +=   5");
        Assert.True(result.IsSyntacticallyValid);
        Assert.Equal("var x = 1\n$x += 5\n", result.FormattedText);
    }

    [Fact]
    public void Full_line_comments_are_preserved()
    {
        var input = """
            # leading note
            var x = 1
            # between
            var y = 2
            """;
        var result = ToshFormatter.Format(input);
        Assert.True(result.IsSyntacticallyValid);
        Assert.Equal(
            """
            # leading note
            var x = 1
            # between
            var y = 2

            """,
            result.FormattedText);
    }

    [Fact]
    public void Trailing_comments_are_preserved()
    {
        var result = ToshFormatter.Format("var x = 1  # the answer");
        Assert.True(result.IsSyntacticallyValid);
        Assert.Equal("var x = 1  # the answer\n", result.FormattedText);
    }

    [Fact]
    public void Comments_inside_blocks_are_preserved()
    {
        var input = """
            func g() {
                # inner note
                echo hi
            }
            """;
        var result = ToshFormatter.Format(input);
        Assert.True(result.IsSyntacticallyValid);
        // The comment must survive the round-trip and stay inside the block.
        Assert.Contains("# inner note", result.FormattedText);
        var second = ToshFormatter.Format(result.FormattedText);
        Assert.Equal(result.FormattedText, second.FormattedText);
    }

    [Fact]
    public void Doc_comments_are_left_untouched()
    {
        // Until the formatter learns to round-trip ## doc-comments,
        // it must leave any source containing them unchanged so it
        // does not silently delete API documentation.
        var input = """
            ## Greets the user.
            ## @param=name The person to greet.
            func greet(name)   {  echo $"hi {$name}" }
            """;
        var result = ToshFormatter.Format(input);
        Assert.True(result.IsSyntacticallyValid);
        Assert.Equal(input, result.FormattedText);
    }
}
