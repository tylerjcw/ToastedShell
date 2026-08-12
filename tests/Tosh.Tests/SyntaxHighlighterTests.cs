using Tosh.Cli;
using Tosh.LanguageServices;
using Tosh.Runtime;
using Tosh.Tome;

namespace Tosh.Tests;

public sealed class SyntaxHighlighterTests
{
    [Fact]
    public void Highlights_valid_commands_as_bold_green()
    {
        var runtime = ToshRuntime.CreateDefault();

        var highlighted = SyntaxHighlighter.Highlight("echo hello", runtime);

        Assert.Contains("\x1b[1;32mecho\x1b[0m", highlighted);
        Assert.Contains("\x1b[32mhello\x1b[0m", highlighted);
    }

    [Fact]
    public void Highlights_invalid_commands_as_red()
    {
        var runtime = ToshRuntime.CreateDefault();

        var highlighted = SyntaxHighlighter.Highlight("no_such_command", runtime);

        Assert.Contains("\x1b[31mno_such_command\x1b[0m", highlighted);
    }

    [Fact]
    public void Highlights_existing_directory_arguments_as_underlined_green()
    {
        var tempRoot = Directory.CreateTempSubdirectory("tosh-highlight-");

        try
        {
            var childDirectory = Path.Combine(tempRoot.FullName, "examples");
            Directory.CreateDirectory(childDirectory);

            var runtime = ToshRuntime.CreateDefault();
            runtime.CurrentDirectory = tempRoot.FullName;

            var highlighted = SyntaxHighlighter.Highlight("cd examples", runtime);

            Assert.Contains("\x1b[4;32mexamples\x1b[0m", highlighted);
        }
        finally
        {
            tempRoot.Delete(recursive: true);
        }
    }

    [Fact]
    public void Highlights_existing_directory_paths_at_command_position_as_underlined_green()
    {
        var tempRoot = Directory.CreateTempSubdirectory("tosh-highlight-dir-");

        try
        {
            var childDirectory = Path.Combine(tempRoot.FullName, "examples");
            Directory.CreateDirectory(childDirectory);

            var runtime = ToshRuntime.CreateDefault();
            runtime.CurrentDirectory = tempRoot.FullName;

            var highlighted = SyntaxHighlighter.Highlight("./examples/", runtime);

            Assert.Contains("\x1b[4;32m./examples/\x1b[0m", highlighted);
        }
        finally
        {
            tempRoot.Delete(recursive: true);
        }
    }

    [Fact]
    public void Highlights_existing_file_paths_at_command_position_as_underlined_green()
    {
        var tempRoot = Directory.CreateTempSubdirectory("tosh-highlight-file-");

        try
        {
            var childDirectory = Path.Combine(tempRoot.FullName, "examples");
            Directory.CreateDirectory(childDirectory);
            File.WriteAllText(Path.Combine(childDirectory, "interop_demo.tosh"), "# demo");

            var runtime = ToshRuntime.CreateDefault();
            runtime.CurrentDirectory = tempRoot.FullName;

            var highlighted = SyntaxHighlighter.Highlight("./examples/interop_demo.tosh", runtime);

            Assert.Contains("\x1b[4;32m./examples/interop_demo.tosh\x1b[0m", highlighted);
        }
        finally
        {
            tempRoot.Delete(recursive: true);
        }
    }

    [Fact]
    public void Uses_runtime_theme_for_command_highlighting()
    {
        var runtime = ToshRuntime.CreateDefault();
        runtime.Config.Theme.Syntax.ValidCommand.Foreground = "bright-magenta";

        var highlighted = SyntaxHighlighter.Highlight("echo hello", runtime);

        Assert.Contains("\x1b[1;95mecho\x1b[0m", highlighted);
    }

    [Fact]
    public void Highlights_intrinsic_literals_as_constants()
    {
        var runtime = ToshRuntime.CreateDefault();
        runtime.Config.Theme.Syntax.Constant.Foreground = "bright-magenta";

        var highlighted = SyntaxHighlighter.Highlight("echo 2d 2026-03-27 127.0.0.1 ::1", runtime);

        Assert.Contains("\x1b[95m2d\x1b[0m", highlighted);
        Assert.Contains("\x1b[95m2026-03-27\x1b[0m", highlighted);
        Assert.Contains("\x1b[95m127.0.0.1\x1b[0m", highlighted);
        Assert.Contains("\x1b[95m::1\x1b[0m", highlighted);
    }

    [Fact]
    public void Highlights_paired_collection_delimiters_as_punctuation()
    {
        var runtime = ToshRuntime.CreateDefault();
        runtime.Config.Theme.Syntax.Punctuation.Foreground = "bright-magenta";

        var highlighted = SyntaxHighlighter.Highlight("{||} {%%} {::}", runtime);

        Assert.Contains("\x1b[95m{|\x1b[0m", highlighted);
        Assert.Contains("\x1b[95m|}\x1b[0m", highlighted);
        Assert.Contains("\x1b[95m{%\x1b[0m", highlighted);
        Assert.Contains("\x1b[95m%}\x1b[0m", highlighted);
        Assert.Contains("\x1b[95m{:\x1b[0m", highlighted);
        Assert.Contains("\x1b[95m:}\x1b[0m", highlighted);
    }

    [Fact]
    public void Quantity_literals_and_conversion_targets_share_unit_highlighting()
    {
        var runtime = ToshRuntime.CreateDefault();
        runtime.Config.Theme.Syntax.UnitLiteral.Foreground = "bright-magenta";

        var highlighted = SyntaxHighlighter.Highlight("2`mi as `ft", runtime);

        Assert.Contains("\x1b[95m2`mi\x1b[0m", highlighted);
        Assert.Contains("\x1b[95m`ft\x1b[0m", highlighted);

        const string source = "2`mi as `ft";
        var semanticUnits = DecodeSemanticTokens(source)
            .Where(token => token.Type == 3)
            .Select(token => token.Text)
            .ToArray();
        Assert.Contains("2`mi", semanticUnits);
        Assert.Contains("`ft", semanticUnits);
    }

    [Fact]
    public void Tome_colorizer_treats_each_paired_collection_delimiter_as_one_punctuation_span()
    {
        const string source = "{||} {%%} {::}";
        var colorizer = new ToshSyntaxColorizer();
        var punctuationStyle = Assert.Single(colorizer.Colorize("(", 0)).AnsiOpen;

        var spans = colorizer.Colorize(source, 0);

        var delimiters = spans
            .Where(span => span.Length == 2)
            .Select(span => (Text: source.Substring(span.Start, span.Length), span.AnsiOpen))
            .ToArray();
        Assert.Equal(["{|", "|}", "{%", "%}", "{:", ":}"], delimiters.Select(item => item.Text));
        Assert.All(delimiters, item => Assert.Equal(punctuationStyle, item.AnsiOpen));
    }

    [Fact]
    public void Semantic_tokens_emit_paired_collection_delimiters_as_whole_operator_tokens()
    {
        const string source = "{||} {%%} {::}";
        var tokens = new ToshLanguageFeatures().GetSemanticTokens(source, "test.tosh");
        var operatorSpans = new List<string>();
        var line = 0;
        var character = 0;

        for (var offset = 0; offset < tokens.Data.Count; offset += 5)
        {
            var deltaLine = tokens.Data[offset];
            line += deltaLine;
            character = deltaLine == 0
                ? character + tokens.Data[offset + 1]
                : tokens.Data[offset + 1];

            if (line == 0 && tokens.Data[offset + 3] == 7)
            {
                operatorSpans.Add(source.Substring(character, tokens.Data[offset + 2]));
            }
        }

        Assert.Equal(["{|", "|}", "{%", "%}", "{:", ":}"], operatorSpans);
    }

    [Fact]
    public void Highlights_builtin_type_alias_at_command_position_as_type()
    {
        // `int x = 5` — 'int' is the type at command position (typed variable declaration)
        var runtime = ToshRuntime.CreateDefault();
        runtime.Config.Theme.Syntax.Type.Foreground = "bright-cyan";

        var highlighted = SyntaxHighlighter.Highlight("int x = 5", runtime);

        Assert.Contains("\x1b[96mint\x1b[0m", highlighted);
    }

    [Fact]
    public void Highlights_type_annotation_after_colon_as_type()
    {
        // `prop X: int` — 'int' after ':' is a type annotation
        var runtime = ToshRuntime.CreateDefault();
        runtime.Config.Theme.Syntax.Type.Foreground = "bright-cyan";

        var highlighted = SyntaxHighlighter.Highlight("prop X: int", runtime);

        Assert.Contains("\x1b[96mint\x1b[0m", highlighted);
    }

    [Fact]
    public void Highlights_return_type_after_arrow_as_type()
    {
        // `func foo() -> string` — 'string' after '->' is a return type
        var runtime = ToshRuntime.CreateDefault();
        runtime.Config.Theme.Syntax.Type.Foreground = "bright-cyan";

        var highlighted = SyntaxHighlighter.Highlight("func foo() -> string", runtime);

        Assert.Contains("\x1b[96mstring\x1b[0m", highlighted);
    }

    [Fact]
    public void Highlights_user_type_after_extends_as_type()
    {
        // `class Point extends _Point` — '_Point' after 'extends' colored as type (heuristic)
        var runtime = ToshRuntime.CreateDefault();
        runtime.Config.Theme.Syntax.Type.Foreground = "bright-cyan";

        var highlighted = SyntaxHighlighter.Highlight("class Point extends _Point", runtime);

        Assert.Contains("\x1b[96m_Point\x1b[0m", highlighted);
    }

    [Fact]
    public void Highlights_declared_class_name_as_type()
    {
        // `class Point` — 'Point' is the name being declared, colored as type
        var runtime = ToshRuntime.CreateDefault();
        runtime.Config.Theme.Syntax.Type.Foreground = "bright-cyan";

        var highlighted = SyntaxHighlighter.Highlight("class Point", runtime);

        Assert.Contains("\x1b[96mPoint\x1b[0m", highlighted);
    }

    [Fact]
    public void Highlights_known_user_class_in_type_position_as_type()
    {
        // User-defined class registered in runtime is colored as type when used in type position
        var runtime = ToshRuntime.CreateDefault();
        runtime.Config.Theme.Syntax.Type.Foreground = "bright-cyan";

        var highlighted = SyntaxHighlighter.Highlight("func foo() -> MyClass", runtime);

        // Without runtime knowledge 'MyClass' still gets Type color via LooksLikeTypeOrNamespace (uppercase)
        Assert.Contains("\x1b[96mMyClass\x1b[0m", highlighted);
    }

    [Fact]
    public void Highlights_constructor_definition_name_as_type()
    {
        // `Point3(x: int, y: int, z: int)` — Point3 at command position gets type color
        var runtime = ToshRuntime.CreateDefault();
        runtime.Config.Theme.Syntax.Type.Foreground = "bright-cyan";

        var highlighted = SyntaxHighlighter.Highlight("Point3(x: int, y: int, z: int)", runtime);

        Assert.Contains("\x1b[96mPoint3\x1b[0m", highlighted);
    }

    [Fact]
    public void Highlights_underscore_prefixed_constructor_name_as_type()
    {
        // `_Point(x: int)` — _Point at command position gets type color (heuristic: _Uppercase)
        var runtime = ToshRuntime.CreateDefault();
        runtime.Config.Theme.Syntax.Type.Foreground = "bright-cyan";

        var highlighted = SyntaxHighlighter.Highlight("_Point(x: int)", runtime);

        Assert.Contains("\x1b[96m_Point\x1b[0m", highlighted);
    }

    [Fact]
    public void Highlights_dotted_user_type_member_access_as_type()
    {
        // `Point.Empty` — Point is uppercase (heuristic type), .Empty is its member
        var runtime = ToshRuntime.CreateDefault();
        runtime.Config.Theme.Syntax.Type.Foreground = "bright-cyan";

        var highlighted = SyntaxHighlighter.Highlight("Point.Empty", runtime);

        Assert.Contains("\x1b[96mPoint\x1b[0m", highlighted);
        Assert.Contains("\x1b[96mEmpty\x1b[0m", highlighted);
    }

    [Fact]
    public void Highlights_dotted_underscore_prefixed_user_type_member_as_type()
    {
        // `_Point.X` — _Point has _Uppercase heuristic pattern
        var runtime = ToshRuntime.CreateDefault();
        runtime.Config.Theme.Syntax.Type.Foreground = "bright-cyan";

        var highlighted = SyntaxHighlighter.Highlight("_Point.X", runtime);

        Assert.Contains("\x1b[96m_Point\x1b[0m", highlighted);
        Assert.Contains("\x1b[96mX\x1b[0m", highlighted);
    }

    /// <summary>
    /// Type names colour in every context a reader meets them — <c>TS-P3-13</c>'s REPL half.
    /// </summary>
    /// <remarks>
    /// The type-context heuristic knew `->` but not `→`, so the pretty-printed arrow — which is
    /// what real profiles contain — left every return annotation uncoloured. `is`/`as` (match
    /// arms, where declared classes appear most) and glued `<` (generic arguments) were never
    /// type contexts at all. Reported from a profile.tosh screenshot, where `→ FileSystemEntry`
    /// and `is Point2D` rendered as plain text.
    /// </remarks>
    [Theory]
    // The Unicode arrow, as the pretty-printer and real profiles write it.
    [InlineData("func up(levels: int) \u2192 int")]
    // ASCII arrow, which already worked — pinned so the two spellings cannot drift apart.
    [InlineData("func up(levels: int) -> int")]
    // Type test in a match arm.
    [InlineData("$x is int")]
    [InlineData("$x is not int")]
    // Conversion.
    [InlineData("$x as int")]
    // Generic argument, glued to the name.
    [InlineData("var xs = list<int")]
    public void Highlights_builtin_types_in_every_type_context(string input)
    {
        var runtime = ToshRuntime.CreateDefault();

        var highlighted = SyntaxHighlighter.Highlight(input, runtime);

        // bright-cyan is the default theme's Type colour.
        Assert.Contains("\x1b[96mint\x1b[0m", highlighted);
    }

    [Fact]
    public void A_comparison_is_not_mistaken_for_a_generic_argument()
    {
        // `a < b` keeps its spacing; only a glued `<` opens a type context, so `int` here is
        // an ordinary argument rather than a type.
        var runtime = ToshRuntime.CreateDefault();

        var highlighted = SyntaxHighlighter.Highlight("echo 3 < int", runtime);

        Assert.DoesNotContain("\x1b[96mint\x1b[0m", highlighted);
    }

    /// <summary>Decodes semantic tokens into (text, type) pairs for one-line sources.</summary>
    private static List<(string Text, int Type)> DecodeSemanticTokens(string source)
    {
        var tokens = new ToshLanguageFeatures().GetSemanticTokens(source, "test.tosh");
        var decoded = new List<(string, int)>();
        var line = 0;
        var character = 0;

        for (var offset = 0; offset < tokens.Data.Count; offset += 5)
        {
            var deltaLine = tokens.Data[offset];
            line += deltaLine;
            character = deltaLine == 0
                ? character + tokens.Data[offset + 1]
                : tokens.Data[offset + 1];

            if (line == 0)
            {
                decoded.Add((
                    source.Substring(character, tokens.Data[offset + 2]),
                    tokens.Data[offset + 3]));
            }
        }

        return decoded;
    }

    [Fact]
    public void Semantic_tokens_leave_generic_angle_brackets_to_the_grammar()
    {
        // Semantic tokens override TextMate wherever they land. `<` and `>` were emitted as
        // `operator`, which painted over the grammar's generic punctuation in `Point2D<T>` —
        // and since the lexer cannot tell a comparison from a generic delimiter, the honest
        // move is to emit nothing and let the grammar's adjacency rule decide (TS-P3-12).
        var decoded = DecodeSemanticTokens("var p = new Point2D<T>(1, 2)");

        Assert.DoesNotContain(decoded, token => token.Text == "<");
        Assert.DoesNotContain(decoded, token => token.Text == ">");
    }

    [Fact]
    public void Semantic_tokens_cover_only_the_head_of_a_dotted_variable()
    {
        // `$this.X` was one variable-typed token across the whole path, flattening the
        // grammar's $this / accessor / member split into a single colour in any theme with
        // semantic highlighting — which is exactly what a screenshot reported (TS-P3-12).
        var decoded = DecodeSemanticTokens("echo ($this.X + $o.Y)");
        var variables = decoded.Where(token => token.Type == 4).Select(token => token.Text).ToArray();

        Assert.Equal(["$this", "$o"], variables);
    }

    [Fact]
    public void Semantic_tokens_still_cover_a_whole_undotted_variable()
    {
        var decoded = DecodeSemanticTokens("echo $name");

        Assert.Contains(("$name", 4), decoded);
    }
}
