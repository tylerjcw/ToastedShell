using Tosh.Runtime;
using Tosh.Language;
using Tosh.Language.Parsing;
using Tosh.LanguageServices;

namespace Tosh.Tests;

public sealed class DocCommentTests
{
    // ── Lexer ──────────────────────────────────────────────────────

    [Fact]
    public void Lexer_emits_DocComment_tokens_for_double_hash_lines()
    {
        var tokens = new ToshLexer("## Hello world").Lex();

        var docToken = Assert.Single(tokens, t => t.Kind == SyntaxTokenKind.DocComment);
        Assert.Equal("Hello world", docToken.Text);
    }

    [Fact]
    public void Lexer_skips_regular_comments_and_does_not_emit_tokens()
    {
        var tokens = new ToshLexer("# just a comment").Lex();

        Assert.DoesNotContain(tokens, t => t.Kind == SyntaxTokenKind.DocComment);
    }

    [Fact]
    public void Lexer_emits_multiple_consecutive_DocComment_tokens()
    {
        var source = """
            ## Line one
            ## Line two
            ## Line three
            """;
        var tokens = new ToshLexer(source).Lex();

        var docTokens = tokens.Where(t => t.Kind == SyntaxTokenKind.DocComment).ToList();
        Assert.Equal(3, docTokens.Count);
        Assert.Equal("Line one", docTokens[0].Text);
        Assert.Equal("Line two", docTokens[1].Text);
        Assert.Equal("Line three", docTokens[2].Text);
    }

    // ── DocComment.Parse ───────────────────────────────────────────

    [Fact]
    public void Parse_returns_null_for_empty_token_list()
    {
        var result = DocComment.Parse(Array.Empty<SyntaxToken>());

        Assert.Null(result);
    }

    [Fact]
    public void Parse_extracts_description_from_plain_lines()
    {
        var tokens = new[]
        {
            new SyntaxToken(SyntaxTokenKind.DocComment, 0, "Computes Fibonacci numbers."),
            new SyntaxToken(SyntaxTokenKind.DocComment, 0, "Returns a list of integers."),
        };

        var doc = DocComment.Parse(tokens);

        Assert.NotNull(doc);
        Assert.Equal("Computes Fibonacci numbers. Returns a list of integers.", doc.Description);
        Assert.Empty(doc.Parameters);
        Assert.Null(doc.Returns);
        Assert.Empty(doc.Examples);
    }

    [Fact]
    public void Parse_extracts_param_descriptions()
    {
        var tokens = new[]
        {
            new SyntaxToken(SyntaxTokenKind.DocComment, 0, "@param=count How many numbers to produce."),
            new SyntaxToken(SyntaxTokenKind.DocComment, 0, "@param=seed Starting value for the sequence."),
        };

        var doc = DocComment.Parse(tokens);

        Assert.NotNull(doc);
        Assert.Equal(2, doc.Parameters.Count);
        Assert.Equal("How many numbers to produce.", doc.Parameters["count"]);
        Assert.Equal("Starting value for the sequence.", doc.Parameters["seed"]);
    }

    [Fact]
    public void Parse_extracts_returns_description()
    {
        var tokens = new[]
        {
            new SyntaxToken(SyntaxTokenKind.DocComment, 0, "@returns The computed result."),
        };

        var doc = DocComment.Parse(tokens);

        Assert.NotNull(doc);
        Assert.Equal("The computed result.", doc.Returns);
    }

    [Fact]
    public void Parse_extracts_examples()
    {
        var tokens = new[]
        {
            new SyntaxToken(SyntaxTokenKind.DocComment, 0, "@example fibonacci 10"),
            new SyntaxToken(SyntaxTokenKind.DocComment, 0, "@example fibonacci 5 | sum"),
        };

        var doc = DocComment.Parse(tokens);

        Assert.NotNull(doc);
        Assert.Equal(2, doc.Examples.Count);
        Assert.Equal("fibonacci 10", doc.Examples[0]);
        Assert.Equal("fibonacci 5 | sum", doc.Examples[1]);
    }

    [Fact]
    public void Parse_handles_all_tags_together()
    {
        var tokens = new[]
        {
            new SyntaxToken(SyntaxTokenKind.DocComment, 0, "Produce Fibonacci numbers."),
            new SyntaxToken(SyntaxTokenKind.DocComment, 0, "@param=count How many to produce."),
            new SyntaxToken(SyntaxTokenKind.DocComment, 0, "@returns A list of Fibonacci numbers."),
            new SyntaxToken(SyntaxTokenKind.DocComment, 0, "@example fibonacci 10"),
        };

        var doc = DocComment.Parse(tokens);

        Assert.NotNull(doc);
        Assert.Equal("Produce Fibonacci numbers.", doc.Description);
        Assert.Single(doc.Parameters);
        Assert.Equal("How many to produce.", doc.Parameters["count"]);
        Assert.Equal("A list of Fibonacci numbers.", doc.Returns);
        Assert.Single(doc.Examples);
        Assert.Equal("fibonacci 10", doc.Examples[0]);
    }

    // ── Parser (AST attachment) ────────────────────────────────────

    [Fact]
    public void Parser_attaches_doc_comment_to_function_definition()
    {
        const string source = """
            ## Greets the user.
            ## @param=name The name to greet.
            ## @returns A greeting string.
            ## @example greet "Alice"
            func greet(name: string) -> string { "Hello, $name!" }
            """;

        var result = ToshParser.Parse(source);

        Assert.Empty(result.Diagnostics);
        var funcDef = Assert.IsType<FunctionDefinitionStatementSyntax>(result.Statement);
        Assert.NotNull(funcDef.DocComment);
        Assert.Equal("Greets the user.", funcDef.DocComment.Description);
        Assert.Equal("The name to greet.", funcDef.DocComment.Parameters["name"]);
        Assert.Equal("A greeting string.", funcDef.DocComment.Returns);
        Assert.Single(funcDef.DocComment.Examples);
        Assert.Equal("greet \"Alice\"", funcDef.DocComment.Examples[0]);
    }

    [Fact]
    public void Parser_function_without_doc_comment_has_null_DocComment()
    {
        var result = ToshParser.Parse("func greet(name) { echo hello $name }");

        Assert.Empty(result.Diagnostics);
        var funcDef = Assert.IsType<FunctionDefinitionStatementSyntax>(result.Statement);
        Assert.Null(funcDef.DocComment);
    }

    [Fact]
    public void Parser_regular_comments_before_function_do_not_create_doc_comment()
    {
        const string source = """
            # This is a regular comment
            func greet(name) { echo hello $name }
            """;

        var result = ToshParser.Parse(source);

        Assert.Empty(result.Diagnostics);
        var funcDef = Assert.IsType<FunctionDefinitionStatementSyntax>(result.Statement);
        Assert.Null(funcDef.DocComment);
    }

    // ── Runtime (FunctionCommand) ──────────────────────────────────

    [Fact]
    public async Task FunctionCommand_Description_uses_doc_comment()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        await engine.ExecuteToListAsync("""
            ## Adds two numbers together.
            func add(a: int, b: int) -> int { $a + $b }
            """);

        var command = engine.Runtime.Commands.Get("add");

        Assert.NotNull(command);
        Assert.Equal("Adds two numbers together.", command.Description);
    }

    [Fact]
    public async Task FunctionCommand_implements_IDocumentedCommand()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        await engine.ExecuteToListAsync("""
            ## Doubles a number.
            ## @param=n The number to double.
            ## @returns The doubled value.
            ## @example double 5
            func double(n: int) -> int { $n * 2 }
            """);

        var command = engine.Runtime.Commands.Get("double");

        Assert.NotNull(command);
        var documented = Assert.IsAssignableFrom<IDocumentedCommand>(command);
        Assert.Equal("The number to double.", documented.ParameterDescriptions["n"]);
        Assert.Equal("The doubled value.", documented.ReturnsDescription);
        Assert.Single(documented.DocExamples);
        Assert.Equal("double 5", documented.DocExamples[0]);
    }

    [Fact]
    public async Task FunctionCommand_without_doc_comment_uses_default_description()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        await engine.ExecuteToListAsync("func greet(name) { echo hello $name }");

        var command = engine.Runtime.Commands.Get("greet");

        Assert.NotNull(command);
        Assert.Equal("User-defined Tosh function.", command.Description);
    }

    // ── Help system integration ────────────────────────────────────

    [Fact]
    public async Task Help_topic_for_documented_function_includes_examples()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        await engine.ExecuteToListAsync("""
            ## Generates Fibonacci numbers.
            ## @param=count How many numbers.
            ## @returns A list of integers.
            ## @example fibonacci 10
            func fibonacci(count: int) -> int { $count }
            """);

        var topic = Assert.IsType<HelpTopic>(Assert.Single(await engine.ExecuteToListAsync("help fibonacci")));

        Assert.Equal("Generates Fibonacci numbers.", topic.Description);
        Assert.Contains("fibonacci 10", topic.Examples);
        Assert.NotNull(topic.Notes);
        Assert.Contains("count", topic.Notes, StringComparison.Ordinal);
        Assert.Contains("Returns:", topic.Notes, StringComparison.Ordinal);
    }

    // ── LSP features ───────────────────────────────────────────────

    [Fact]
    public void Completion_items_include_doc_comment_description()
    {
        var features = new ToshLanguageFeatures();
        const string script = """
            ## Greets someone by name.
            func greet(name: string) { echo hello $name }
            gre
            """;

        var items = features.GetCompletionItems(script, new LspPosition(2, 3));
        var greet = Assert.Single(items, item => item.Label == "greet");

        Assert.Contains("Greets someone by name.", greet.Documentation, StringComparison.Ordinal);
    }

    [Fact]
    public void Semantic_tokens_distinguish_doc_comments_from_regular_comments()
    {
        var features = new ToshLanguageFeatures();
        const string script = """
            # regular comment
            ## doc comment
            func greet() { echo hi }
            """;

        var tokens = features.GetSemanticTokens(script, "test.tosh");
        var data = tokens.Data;

        // First comment token (regular): line 0, modifiers should be 0
        Assert.True(data.Count >= 10, "Expected at least 2 semantic tokens");
        Assert.Equal(0, data[0]); // deltaLine = 0
        Assert.Equal(0, data[4]); // modifiers = 0 (regular comment)

        // Second comment token (doc): line 1, modifiers should have documentation bit (0x04)
        Assert.Equal(1, data[5]); // deltaLine = 1
        Assert.Equal(0x04, data[9]); // modifiers = documentation
    }

    // ── Parser (all declaration types) ─────────────────────────────

    [Fact]
    public void Parser_attaches_doc_comment_to_class_definition()
    {
        const string source = """
            ## A simple point class.
            class Point {
                prop x: int
                prop y: int
            }
            """;

        var result = ToshParser.Parse(source);

        Assert.Empty(result.Diagnostics);
        var classDef = Assert.IsType<ClassDefinitionStatementSyntax>(result.Statement);
        Assert.NotNull(classDef.DocComment);
        Assert.Equal("A simple point class.", classDef.DocComment.Description);
    }

    [Fact]
    public void Parser_attaches_doc_comment_to_module_definition()
    {
        const string source = """
            ## Math utilities module.
            module MathUtils {
                func add(a: int, b: int) -> int { $a + $b }
            }
            """;

        var result = ToshParser.Parse(source);

        Assert.Empty(result.Diagnostics);
        var moduleDef = Assert.IsType<ModuleDefinitionStatementSyntax>(result.Statement);
        Assert.NotNull(moduleDef.DocComment);
        Assert.Equal("Math utilities module.", moduleDef.DocComment.Description);
    }

    [Fact]
    public void Parser_attaches_doc_comment_to_enum_definition()
    {
        const string source = """
            ## Represents cardinal directions.
            enum Direction { North; South; East; West }
            """;

        var result = ToshParser.Parse(source);

        Assert.Empty(result.Diagnostics);
        var enumDef = Assert.IsType<EnumDefinitionStatementSyntax>(result.Statement);
        Assert.NotNull(enumDef.DocComment);
        Assert.Equal("Represents cardinal directions.", enumDef.DocComment.Description);
    }

    [Fact]
    public void Parser_attaches_doc_comment_to_record_definition()
    {
        const string source = """
            ## An immutable person record.
            record Person(name: string, age: int)
            """;

        var result = ToshParser.Parse(source);

        Assert.Empty(result.Diagnostics);
        var recordDef = Assert.IsType<RecordDefinitionStatementSyntax>(result.Statement);
        Assert.NotNull(recordDef.DocComment);
        Assert.Equal("An immutable person record.", recordDef.DocComment.Description);
    }

    [Fact]
    public void Parser_attaches_doc_comment_to_event_definition()
    {
        const string source = """
            ## Fires when a file changes.
            event FileChanged { path }
            """;

        var result = ToshParser.Parse(source);

        Assert.Empty(result.Diagnostics);
        var eventDef = Assert.IsType<EventDefinitionStatementSyntax>(result.Statement);
        Assert.NotNull(eventDef.DocComment);
        Assert.Equal("Fires when a file changes.", eventDef.DocComment.Description);
    }

    [Fact]
    public void Parser_attaches_doc_comment_to_class_method()
    {
        const string source = """
            class Calculator {
                ## Adds two numbers.
                ## @param=a First number.
                ## @param=b Second number.
                func add(a: int, b: int) -> int { $a + $b }
            }
            """;

        var result = ToshParser.Parse(source);

        Assert.Empty(result.Diagnostics);
        var classDef = Assert.IsType<ClassDefinitionStatementSyntax>(result.Statement);
        var method = classDef.Members.OfType<ClassMethodMemberSyntax>().Single();
        Assert.NotNull(method.Method.DocComment);
        Assert.Equal("Adds two numbers.", method.Method.DocComment.Description);
        Assert.Equal("First number.", method.Method.DocComment.Parameters["a"]);
        Assert.Equal("Second number.", method.Method.DocComment.Parameters["b"]);
    }

    [Fact]
    public void Parser_attaches_doc_comment_to_class_property()
    {
        const string source = """
            class Config {
                ## The maximum retry count.
                prop maxRetries: int
            }
            """;

        var result = ToshParser.Parse(source);

        Assert.Empty(result.Diagnostics);
        var classDef = Assert.IsType<ClassDefinitionStatementSyntax>(result.Statement);
        var property = classDef.Members.OfType<ClassPropertyMemberSyntax>().Single();
        Assert.NotNull(property.DocComment);
        Assert.Equal("The maximum retry count.", property.DocComment.Description);
    }

    [Fact]
    public void Parser_class_without_doc_comment_has_null_DocComment()
    {
        var result = ToshParser.Parse("class Point { prop x: int; prop y: int }");

        Assert.Empty(result.Diagnostics);
        var classDef = Assert.IsType<ClassDefinitionStatementSyntax>(result.Statement);
        Assert.Null(classDef.DocComment);
    }

    // ── LSP features for declaration types ─────────────────────────

    [Fact]
    public void Hover_shows_doc_comment_for_class()
    {
        var features = new ToshLanguageFeatures();
        const string script = """
            ## A 2D point.
            class Point {
                prop x: int
                prop y: int
            }
            $p = Point
            """;

        // Hover over "Point" on the last line
        var hover = features.GetHover(script, "test.tosh", new LspPosition(5, 5));

        Assert.NotNull(hover);
        Assert.Contains("A 2D point.", hover.Contents.Value, StringComparison.Ordinal);
        Assert.Contains("Class", hover.Contents.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void Hover_shows_doc_comment_for_record()
    {
        var features = new ToshLanguageFeatures();
        const string script = """
            ## An immutable person.
            record Person(name: string, age: int)
            $p = Person
            """;

        var hover = features.GetHover(script, "test.tosh", new LspPosition(2, 5));

        Assert.NotNull(hover);
        Assert.Contains("An immutable person.", hover.Contents.Value, StringComparison.Ordinal);
        Assert.Contains("Record", hover.Contents.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void Completion_includes_doc_description_for_type()
    {
        var features = new ToshLanguageFeatures();
        const string script = """
            ## A 2D point.
            class Point {
                prop x: int
                prop y: int
            }
            Poi
            """;

        var items = features.GetCompletionItems(script, new LspPosition(5, 3));
        var point = Assert.Single(items, item => item.Label == "Point");

        Assert.Contains("A 2D point.", point.Documentation, StringComparison.Ordinal);
    }

    [Fact]
    public void Completion_includes_doc_description_for_module()
    {
        var features = new ToshLanguageFeatures();
        const string script = """
            ## Math utilities.
            module MathUtils {
                func add(a: int, b: int) -> int { $a + $b }
            }
            Math
            """;

        var items = features.GetCompletionItems(script, new LspPosition(4, 4));
        var mathUtils = Assert.Single(items, item => item.Label == "MathUtils");

        Assert.Contains("Math utilities.", mathUtils.Documentation, StringComparison.Ordinal);
    }

    // ── New tag parsing ────────────────────────────────────────────

    [Fact]
    public void Parse_extracts_deprecated_with_message()
    {
        var tokens = new[]
        {
            new SyntaxToken(SyntaxTokenKind.DocComment, 0, "Old function."),
            new SyntaxToken(SyntaxTokenKind.DocComment, 0, "@deprecated Use `fetch` instead."),
        };

        var doc = DocComment.Parse(tokens);

        Assert.NotNull(doc);
        Assert.True(doc.IsDeprecated);
        Assert.Equal("Use `fetch` instead.", doc.Deprecated);
        Assert.Equal("Old function.", doc.Description);
    }

    [Fact]
    public void Parse_extracts_bare_deprecated()
    {
        var tokens = new[]
        {
            new SyntaxToken(SyntaxTokenKind.DocComment, 0, "@deprecated"),
        };

        var doc = DocComment.Parse(tokens);

        Assert.NotNull(doc);
        Assert.True(doc.IsDeprecated);
        Assert.Null(doc.Deprecated);
    }

    [Fact]
    public void Parse_extracts_see_also()
    {
        var tokens = new[]
        {
            new SyntaxToken(SyntaxTokenKind.DocComment, 0, "Parses JSON."),
            new SyntaxToken(SyntaxTokenKind.DocComment, 0, "@see to-json"),
            new SyntaxToken(SyntaxTokenKind.DocComment, 0, "@see from-yaml"),
        };

        var doc = DocComment.Parse(tokens);

        Assert.NotNull(doc);
        Assert.NotNull(doc.SeeAlso);
        Assert.Equal(2, doc.SeeAlso.Count);
        Assert.Equal("to-json", doc.SeeAlso[0]);
        Assert.Equal("from-yaml", doc.SeeAlso[1]);
    }

    [Fact]
    public void Parse_extracts_since()
    {
        var tokens = new[]
        {
            new SyntaxToken(SyntaxTokenKind.DocComment, 0, "New command."),
            new SyntaxToken(SyntaxTokenKind.DocComment, 0, "@since 0.9.0"),
        };

        var doc = DocComment.Parse(tokens);

        Assert.NotNull(doc);
        Assert.Equal("0.9.0", doc.Since);
    }

    [Fact]
    public void Parse_extracts_throws()
    {
        var tokens = new[]
        {
            new SyntaxToken(SyntaxTokenKind.DocComment, 0, "Reads a file."),
            new SyntaxToken(SyntaxTokenKind.DocComment, 0, "@throws When the file does not exist."),
            new SyntaxToken(SyntaxTokenKind.DocComment, 0, "@throws When the file is not readable."),
        };

        var doc = DocComment.Parse(tokens);

        Assert.NotNull(doc);
        Assert.NotNull(doc.Throws);
        Assert.Equal(2, doc.Throws.Count);
        Assert.Equal("When the file does not exist.", doc.Throws[0]);
        Assert.Equal("When the file is not readable.", doc.Throws[1]);
    }

    [Fact]
    public void Parse_handles_all_new_tags_together()
    {
        var tokens = new[]
        {
            new SyntaxToken(SyntaxTokenKind.DocComment, 0, "Fetches a URL."),
            new SyntaxToken(SyntaxTokenKind.DocComment, 0, "@param=url The URL to fetch."),
            new SyntaxToken(SyntaxTokenKind.DocComment, 0, "@returns The response body."),
            new SyntaxToken(SyntaxTokenKind.DocComment, 0, "@throws On network failure."),
            new SyntaxToken(SyntaxTokenKind.DocComment, 0, "@since 1.0.0"),
            new SyntaxToken(SyntaxTokenKind.DocComment, 0, "@see http-post"),
            new SyntaxToken(SyntaxTokenKind.DocComment, 0, "@example fetch \"https://example.com\""),
        };

        var doc = DocComment.Parse(tokens);

        Assert.NotNull(doc);
        Assert.Equal("Fetches a URL.", doc.Description);
        Assert.Equal("The URL to fetch.", doc.Parameters["url"]);
        Assert.Equal("The response body.", doc.Returns);
        Assert.Single(doc.Throws!);
        Assert.Equal("On network failure.", doc.Throws![0]);
        Assert.Equal("1.0.0", doc.Since);
        Assert.Single(doc.SeeAlso!);
        Assert.Equal("http-post", doc.SeeAlso![0]);
        Assert.Single(doc.Examples);
    }

    // ── Multi-line @param ──────────────────────────────────────────

    [Fact]
    public void Parse_multiline_param_appends_indented_continuation()
    {
        var tokens = new[]
        {
            new SyntaxToken(SyntaxTokenKind.DocComment, 0, "@param=options A record of configuration flags."),
            new SyntaxToken(SyntaxTokenKind.DocComment, 0, "  Supports: timeout, retries, headers."),
            new SyntaxToken(SyntaxTokenKind.DocComment, 0, "  See docs for full list."),
        };

        var doc = DocComment.Parse(tokens);

        Assert.NotNull(doc);
        Assert.Equal(
            "A record of configuration flags. Supports: timeout, retries, headers. See docs for full list.",
            doc.Parameters["options"]);
    }

    [Fact]
    public void Parse_multiline_param_stops_at_next_tag()
    {
        var tokens = new[]
        {
            new SyntaxToken(SyntaxTokenKind.DocComment, 0, "@param=url The URL to fetch."),
            new SyntaxToken(SyntaxTokenKind.DocComment, 0, "  Must be a valid HTTP URL."),
            new SyntaxToken(SyntaxTokenKind.DocComment, 0, "@returns The response body."),
        };

        var doc = DocComment.Parse(tokens);

        Assert.NotNull(doc);
        Assert.Equal("The URL to fetch. Must be a valid HTTP URL.", doc.Parameters["url"]);
        Assert.Equal("The response body.", doc.Returns);
    }

    // ── Hover rendering of new tags ────────────────────────────────

    [Fact]
    public void Hover_shows_deprecated_for_function()
    {
        var features = new ToshLanguageFeatures();
        const string script = """
            ## Old function.
            ## @deprecated Use `fetch` instead.
            func http-get(url: string) { echo $url }
            http-get
            """;

        var hover = features.GetHover(script, "test.tosh", new LspPosition(3, 2));

        Assert.NotNull(hover);
        Assert.Contains("@deprecated", hover.Contents.Value, StringComparison.Ordinal);
        Assert.Contains("Use `fetch` instead.", hover.Contents.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void Hover_shows_throws_and_since_for_function()
    {
        var features = new ToshLanguageFeatures();
        const string script = """
            ## Reads a config file.
            ## @throws When the file is missing.
            ## @since 0.8.0
            ## @see write-config
            func read-config(path: string) { echo $path }
            read-config
            """;

        var hover = features.GetHover(script, "test.tosh", new LspPosition(5, 2));

        Assert.NotNull(hover);
        Assert.Contains("@throws", hover.Contents.Value, StringComparison.Ordinal);
        Assert.Contains("When the file is missing.", hover.Contents.Value, StringComparison.Ordinal);
        Assert.Contains("@since", hover.Contents.Value, StringComparison.Ordinal);
        Assert.Contains("0.8.0", hover.Contents.Value, StringComparison.Ordinal);
        Assert.Contains("@see", hover.Contents.Value, StringComparison.Ordinal);
        Assert.Contains("write-config", hover.Contents.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void Hover_shows_deprecated_for_class()
    {
        var features = new ToshLanguageFeatures();
        const string script = """
            ## @deprecated Use `Point3D` instead.
            class OldPoint {
                prop x: int
            }
            $p = OldPoint
            """;

        var hover = features.GetHover(script, "test.tosh", new LspPosition(4, 6));

        Assert.NotNull(hover);
        Assert.Contains("@deprecated", hover.Contents.Value, StringComparison.Ordinal);
        Assert.Contains("Use `Point3D` instead.", hover.Contents.Value, StringComparison.Ordinal);
    }

    // ── Completion deprecated tag ──────────────────────────────────

    [Fact]
    public void Completion_deprecated_function_has_deprecated_tag()
    {
        var features = new ToshLanguageFeatures();
        const string script = """
            ## @deprecated Use `fetch`.
            func http-get(url: string) { echo $url }
            http
            """;

        var items = features.GetCompletionItems(script, new LspPosition(2, 4));
        var httpGet = Assert.Single(items, item => item.Label == "http-get");

        Assert.NotNull(httpGet.Tags);
        Assert.Contains(1, httpGet.Tags);
    }

    [Fact]
    public void Completion_non_deprecated_function_has_no_tags()
    {
        var features = new ToshLanguageFeatures();
        const string script = """
            ## A normal function.
            func greet(name: string) { echo hello $name }
            gre
            """;

        var items = features.GetCompletionItems(script, new LspPosition(2, 3));
        var greet = Assert.Single(items, item => item.Label == "greet");

        Assert.Null(greet.Tags);
    }

    // ── Signature help with doc comments ───────────────────────────

    [Fact]
    public void Signature_help_shows_param_descriptions()
    {
        var features = new ToshLanguageFeatures();
        const string script = """
            ## Adds numbers.
            ## @param=a The first number.
            ## @param=b The second number.
            func add(a: int, b: int) -> int { $a + $b }
            add 1
            """;

        var help = features.GetSignatureHelp(script, "test.tosh", new LspPosition(4, 5));

        Assert.NotNull(help);
        Assert.NotEmpty(help.Signatures);
        var sig = help.Signatures[0];
        Assert.Equal("Adds numbers.", sig.Documentation);
        Assert.NotNull(sig.Parameters);
        Assert.Equal(2, sig.Parameters.Count);
        Assert.Equal("The first number.", sig.Parameters[0].Documentation);
        Assert.Equal("The second number.", sig.Parameters[1].Documentation);
    }

    // ── Property/method hover with doc comments ────────────────────

    [Fact]
    public void Hover_shows_doc_comment_on_class_property_access()
    {
        var features = new ToshLanguageFeatures();
        const string script = """
            class Config {
                ## The maximum retry count.
                prop maxRetries: int
                func show() { $this.maxRetries }
            }
            """;

        // Hover over maxRetries in $this.maxRetries inside the class
        var hover = features.GetHover(script, "test.tosh", new LspPosition(3, 30));

        Assert.NotNull(hover);
        Assert.Contains("The maximum retry count.", hover.Contents.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void Hover_shows_doc_comment_on_class_method_access()
    {
        var features = new ToshLanguageFeatures();
        const string script = """
            class Calculator {
                ## Adds two numbers together.
                func add(a: int, b: int) -> int { $a + $b }
                func run() { $this.add }
            }
            """;

        // Hover over add in $this.add inside the class
        var hover = features.GetHover(script, "test.tosh", new LspPosition(3, 26));

        Assert.NotNull(hover);
        Assert.Contains("Adds two numbers together.", hover.Contents.Value, StringComparison.Ordinal);
    }

    // ── Underscore name regression ─────────────────────────────────

    [Fact]
    public void Hover_works_on_function_with_underscores()
    {
        var features = new ToshLanguageFeatures();
        const string script = """
            ## Builds the publish arguments.
            func build_publish_args() { echo hello }
            build_publish_args
            """;

        var hover = features.GetHover(script, "test.tosh", new LspPosition(2, 8));

        Assert.NotNull(hover);
        Assert.Contains("Builds the publish arguments.", hover.Contents.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void Hover_works_on_class_with_underscores()
    {
        var features = new ToshLanguageFeatures();
        const string script = """
            ## A test class.
            class my_test_class { prop x: int }
            $p = my_test_class
            """;

        var hover = features.GetHover(script, "test.tosh", new LspPosition(2, 8));

        Assert.NotNull(hover);
        Assert.Contains("A test class.", hover.Contents.Value, StringComparison.Ordinal);
    }
}
