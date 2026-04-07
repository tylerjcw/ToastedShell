using Tosh.Core;

namespace Tosh.Tests;

/// <summary>
/// Tests the command introspection pipeline: attribute metadata extraction,
/// manifest export, and LaTeX emission.
/// </summary>
public sealed class CommandIntrospectionTests
{
    // ── Helpers ───────────────────────────────────────────────────────────

    [CommandCategory("Testing")]
    [CommandArgument("path", "Target path.", Required = true, TypeName = "path")]
    [CommandArgument("name", "Optional name.", Required = false)]
    [CommandOption("-r, --recursive", "Process recursively.")]
    [CommandOption("-v", "Verbose output.")]
    [CommandExample("test-cmd /tmp", Title = "Basic usage")]
    [CommandExample("test-cmd /tmp -r")]
    [CommandNote("This is a test note.")]
    [CommandOutput("A list of processed items.")]
    [PipelineInput(AcceptsList = true, AcceptsScalar = true, Description = "Items to process.")]
    private sealed class FullyAnnotatedCommand : ShellCommand
    {
        public FullyAnnotatedCommand()
            : base("test-cmd", "A test command.", "test-cmd <path> [name] [-r] [-v]") { }

        public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    [CommandCategory("Minimal")]
    private sealed class MinimalCommand : ShellCommand
    {
        public MinimalCommand()
            : base("minimal", "A minimal command.", "minimal") { }

        public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class UnannotatedCommand : ShellCommand
    {
        public UnannotatedCommand()
            : base("unannotated", "No attributes.", "unannotated [args]") { }

        public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private static ShellCommandRegistry CreateRegistry(params IShellCommand[] commands)
    {
        var registry = new ShellCommandRegistry();
        foreach (var cmd in commands)
            registry.Register(cmd);
        return registry;
    }

    // ── BuildManifest ────────────────────────────────────────────────────

    [Fact]
    public void BuildManifest_extracts_category_from_attribute()
    {
        var registry = CreateRegistry(new FullyAnnotatedCommand());
        var manifest = CommandManifestExporter.BuildManifest(registry);

        var entry = Assert.Single(manifest);
        Assert.Equal("Testing", entry.Category);
    }

    [Fact]
    public void BuildManifest_extracts_arguments_from_attributes()
    {
        var registry = CreateRegistry(new FullyAnnotatedCommand());
        var entry = Assert.Single(CommandManifestExporter.BuildManifest(registry));

        Assert.Equal(2, entry.Arguments.Count);

        Assert.Equal("path", entry.Arguments[0].Name);
        Assert.Equal("Target path.", entry.Arguments[0].Description);
        Assert.True(entry.Arguments[0].Required);
        Assert.Equal("path", entry.Arguments[0].TypeName);

        Assert.Equal("name", entry.Arguments[1].Name);
        Assert.False(entry.Arguments[1].Required);
        Assert.Null(entry.Arguments[1].TypeName);
    }

    [Fact]
    public void BuildManifest_extracts_options_from_attributes()
    {
        var registry = CreateRegistry(new FullyAnnotatedCommand());
        var entry = Assert.Single(CommandManifestExporter.BuildManifest(registry));

        Assert.Equal(2, entry.Options.Count);
        Assert.Equal("-r, --recursive", entry.Options[0].Syntax);
        Assert.Equal("Process recursively.", entry.Options[0].Description);
        Assert.Equal("-v", entry.Options[1].Syntax);
    }

    [Fact]
    public void BuildManifest_extracts_examples_from_attributes()
    {
        var registry = CreateRegistry(new FullyAnnotatedCommand());
        var entry = Assert.Single(CommandManifestExporter.BuildManifest(registry));

        Assert.Equal(2, entry.Examples.Count);
        Assert.Equal("test-cmd /tmp", entry.Examples[0].Code);
        Assert.Equal("Basic usage", entry.Examples[0].Title);
        Assert.Equal("test-cmd /tmp -r", entry.Examples[1].Code);
        Assert.Null(entry.Examples[1].Title);
    }

    [Fact]
    public void BuildManifest_extracts_notes_from_attributes()
    {
        var registry = CreateRegistry(new FullyAnnotatedCommand());
        var entry = Assert.Single(CommandManifestExporter.BuildManifest(registry));

        Assert.Single(entry.Notes);
        Assert.Equal("This is a test note.", entry.Notes[0]);
    }

    [Fact]
    public void BuildManifest_extracts_output_from_attribute()
    {
        var registry = CreateRegistry(new FullyAnnotatedCommand());
        var entry = Assert.Single(CommandManifestExporter.BuildManifest(registry));

        Assert.Equal("A list of processed items.", entry.Output);
    }

    [Fact]
    public void BuildManifest_extracts_pipeline_input_from_attribute()
    {
        var registry = CreateRegistry(new FullyAnnotatedCommand());
        var entry = Assert.Single(CommandManifestExporter.BuildManifest(registry));

        Assert.NotNull(entry.PipelineInput);
        Assert.True(entry.PipelineInput.AcceptsScalar);
        Assert.True(entry.PipelineInput.AcceptsList);
        Assert.False(entry.PipelineInput.AcceptsRecord);
        Assert.False(entry.PipelineInput.AcceptsTable);
        Assert.Equal("Items to process.", entry.PipelineInput.Description);
    }

    [Fact]
    public void BuildManifest_minimal_command_has_empty_collections()
    {
        var registry = CreateRegistry(new MinimalCommand());
        var entry = Assert.Single(CommandManifestExporter.BuildManifest(registry));

        Assert.Equal("Minimal", entry.Category);
        Assert.Equal("minimal", entry.Name);
        Assert.Empty(entry.Arguments);
        Assert.Empty(entry.Options);
        Assert.Empty(entry.Examples);
        Assert.Empty(entry.Notes);
        Assert.Null(entry.Output);
        Assert.Null(entry.PipelineInput);
    }

    [Fact]
    public void BuildManifest_preserves_name_description_usage()
    {
        var registry = CreateRegistry(new FullyAnnotatedCommand());
        var entry = Assert.Single(CommandManifestExporter.BuildManifest(registry));

        Assert.Equal("test-cmd", entry.Name);
        Assert.Equal("A test command.", entry.Description);
        Assert.Equal("test-cmd <path> [name] [-r] [-v]", entry.Usage);
    }

    [Fact]
    public void BuildManifest_orders_by_category_then_name()
    {
        var registry = CreateRegistry(new MinimalCommand(), new FullyAnnotatedCommand());
        var manifest = CommandManifestExporter.BuildManifest(registry);

        Assert.Equal(2, manifest.Count);
        Assert.Equal("Minimal", manifest[0].Category);
        Assert.Equal("Testing", manifest[1].Category);
    }

    // ── ExportJson ───────────────────────────────────────────────────────

    [Fact]
    public void ExportJson_produces_valid_json_with_camelCase()
    {
        var registry = CreateRegistry(new FullyAnnotatedCommand());
        var json = CommandManifestExporter.ExportJson(registry);

        Assert.Contains("\"name\":", json);
        Assert.Contains("\"category\":", json);
        Assert.Contains("\"test-cmd\"", json);
        Assert.DoesNotContain("\"Name\":", json);
    }

    // ── LaTeX Emitter ────────────────────────────────────────────────────

    [Fact]
    public void Emit_generates_section_per_category()
    {
        var registry = CreateRegistry(new FullyAnnotatedCommand(), new MinimalCommand());
        var manifest = CommandManifestExporter.BuildManifest(registry);
        var latex = CommandLatexEmitter.Emit(manifest);

        Assert.Contains("\\section{Testing}", latex);
        Assert.Contains("\\section{Minimal}", latex);
    }

    [Fact]
    public void Emit_generates_subsection_per_command()
    {
        var registry = CreateRegistry(new FullyAnnotatedCommand());
        var manifest = CommandManifestExporter.BuildManifest(registry);
        var latex = CommandLatexEmitter.Emit(manifest);

        Assert.Contains("\\subsection{\\texttt{test-cmd}}", latex);
        Assert.Contains("\\label{ref:test-cmd}", latex);
        Assert.Contains("\\icmd{test-cmd}", latex);
    }

    [Fact]
    public void Emit_generates_cmdbox_with_description()
    {
        var registry = CreateRegistry(new FullyAnnotatedCommand());
        var manifest = CommandManifestExporter.BuildManifest(registry);
        var latex = CommandLatexEmitter.Emit(manifest);

        Assert.Contains("\\begin{cmdbox}{test-cmd}", latex);
        Assert.Contains("A test command.", latex);
        Assert.Contains("\\end{cmdbox}", latex);
    }

    [Fact]
    public void Emit_generates_signature()
    {
        var registry = CreateRegistry(new FullyAnnotatedCommand());
        var manifest = CommandManifestExporter.BuildManifest(registry);
        var latex = CommandLatexEmitter.Emit(manifest);

        Assert.Contains("\\begin{signature}", latex);
        Assert.Contains("\\end{signature}", latex);
    }

    [Fact]
    public void Emit_generates_arguments_table()
    {
        var registry = CreateRegistry(new FullyAnnotatedCommand());
        var manifest = CommandManifestExporter.BuildManifest(registry);
        var latex = CommandLatexEmitter.Emit(manifest);

        Assert.Contains("\\textbf{Argument}", latex);
        Assert.Contains("path", latex);
        Assert.Contains("Target path.", latex);
        Assert.Contains("\\textit{(path)}", latex);     // type annotation
        Assert.Contains("(optional)", latex);             // name is optional
    }

    [Fact]
    public void Emit_generates_options_table()
    {
        var registry = CreateRegistry(new FullyAnnotatedCommand());
        var manifest = CommandManifestExporter.BuildManifest(registry);
        var latex = CommandLatexEmitter.Emit(manifest);

        Assert.Contains("\\textbf{Flag / Option}", latex);
        Assert.Contains("-r, --recursive", latex);
        Assert.Contains("Process recursively.", latex);
    }

    [Fact]
    public void Emit_generates_examples_in_lstlisting()
    {
        var registry = CreateRegistry(new FullyAnnotatedCommand());
        var manifest = CommandManifestExporter.BuildManifest(registry);
        var latex = CommandLatexEmitter.Emit(manifest);

        Assert.Contains("\\begin{lstlisting}", latex);
        Assert.Contains("test-cmd /tmp  # Basic usage", latex);
        Assert.Contains("test-cmd /tmp -r", latex);
        Assert.Contains("\\end{lstlisting}", latex);
    }

    [Fact]
    public void Emit_generates_notes_in_notebox()
    {
        var registry = CreateRegistry(new FullyAnnotatedCommand());
        var manifest = CommandManifestExporter.BuildManifest(registry);
        var latex = CommandLatexEmitter.Emit(manifest);

        Assert.Contains("\\begin{notebox}", latex);
        Assert.Contains("This is a test note.", latex);
        Assert.Contains("\\end{notebox}", latex);
    }

    [Fact]
    public void Emit_generates_output_description()
    {
        var registry = CreateRegistry(new FullyAnnotatedCommand());
        var manifest = CommandManifestExporter.BuildManifest(registry);
        var latex = CommandLatexEmitter.Emit(manifest);

        Assert.Contains("\\textbf{Output:} A list of processed items.", latex);
    }

    [Fact]
    public void Emit_generates_pipeline_input_info()
    {
        var registry = CreateRegistry(new FullyAnnotatedCommand());
        var manifest = CommandManifestExporter.BuildManifest(registry);
        var latex = CommandLatexEmitter.Emit(manifest);

        Assert.Contains("\\textbf{Pipeline input:} scalar, list", latex);
        Assert.Contains("Items to process.", latex);
    }

    [Fact]
    public void Emit_skips_empty_sections_for_minimal_command()
    {
        var registry = CreateRegistry(new MinimalCommand());
        var manifest = CommandManifestExporter.BuildManifest(registry);
        var latex = CommandLatexEmitter.Emit(manifest);

        Assert.DoesNotContain("\\textbf{Argument}", latex);
        Assert.DoesNotContain("\\textbf{Flag / Option}", latex);
        Assert.DoesNotContain("\\begin{lstlisting}", latex);
        Assert.DoesNotContain("\\begin{notebox}", latex);
        Assert.DoesNotContain("\\textbf{Output:}", latex);
        Assert.DoesNotContain("\\textbf{Pipeline input:}", latex);
    }

    // ── EscapeLatex ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("hello", "hello")]
    [InlineData("a & b", "a \\& b")]
    [InlineData("100%", "100\\%")]
    [InlineData("$var", "\\$var")]
    [InlineData("a_b", "a\\_b")]
    [InlineData("a#b", "a\\#b")]
    [InlineData("{}", "\\{\\}")]
    [InlineData("a^b", "a\\^{}b")]
    [InlineData("a~b", "a\\textasciitilde{}b")]
    [InlineData("back\\slash", "back\\textbackslash{}slash")]
    public void EscapeLatex_handles_special_characters(string input, string expected)
    {
        // EscapeLatex is private, so we test it indirectly through Emit.
        // Create a manifest entry whose description contains the special chars.
        var entry = new CommandManifestEntry(
            Name: "esc-test",
            Description: input,
            Usage: "esc-test",
            Category: "Test",
            Aliases: [],
            Arguments: [],
            Options: [],
            Examples: [],
            Notes: [],
            Output: null,
            PipelineInput: null);

        var latex = CommandLatexEmitter.Emit([entry]);
        Assert.Contains(expected, latex);
    }

    // ── FormatUsage ──────────────────────────────────────────────────────

    [Fact]
    public void Emit_short_usage_stays_on_one_line()
    {
        var entry = new CommandManifestEntry(
            Name: "short",
            Description: "Short.",
            Usage: "short -a",
            Category: "Test",
            Aliases: [], Arguments: [], Options: [],
            Examples: [], Notes: [], Output: null, PipelineInput: null);

        var latex = CommandLatexEmitter.Emit([entry]);
        // Should not contain continuation marker
        Assert.DoesNotContain("\\quad", latex);
    }

    [Fact]
    public void Emit_long_usage_wraps_with_continuation()
    {
        var longUsage = "find <path> [-name <pattern>] [-type <f|d>] [-maxdepth <n>] [-mindepth <n>] [-size <spec>] [-mtime <n>] [-newer <file>] [-perm <mode>] [-exec <command>]";
        var entry = new CommandManifestEntry(
            Name: "find",
            Description: "Find files.",
            Usage: longUsage,
            Category: "Test",
            Aliases: [], Arguments: [], Options: [],
            Examples: [], Notes: [], Output: null, PipelineInput: null);

        var latex = CommandLatexEmitter.Emit([entry]);
        Assert.Contains("\\quad", latex);
    }

    [Fact]
    public void Emit_pipe_delimited_tokens_get_allowbreak()
    {
        // Token >28 chars with pipes should get \allowbreak — usage must exceed 72 chars to enter the word-splitting branch
        var usage = "http <get|post|put|patch|delete|head|options> <url> [-H <header>] [-d <body>] [--timeout <ms>]";
        var entry = new CommandManifestEntry(
            Name: "http",
            Description: "HTTP.",
            Usage: usage,
            Category: "Test",
            Aliases: [], Arguments: [], Options: [],
            Examples: [], Notes: [], Output: null, PipelineInput: null);

        var latex = CommandLatexEmitter.Emit([entry]);
        Assert.Contains("\\allowbreak", latex);
    }

    // ── EmitFromJson round-trip ──────────────────────────────────────────

    [Fact]
    public void EmitFromJson_roundtrips_through_json()
    {
        var registry = CreateRegistry(new FullyAnnotatedCommand());
        var json = CommandManifestExporter.ExportJson(registry);
        var latex = CommandLatexEmitter.EmitFromJson(json);

        Assert.Contains("\\section{Testing}", latex);
        Assert.Contains("\\subsection{\\texttt{test-cmd}}", latex);
        Assert.Contains("A test command.", latex);
    }

    // ── Bracket-at-cell-start guard ──────────────────────────────────────

    [Fact]
    public void Emit_argument_starting_with_bracket_gets_guard()
    {
        var entry = new CommandManifestEntry(
            Name: "test",
            Description: "Test.",
            Usage: "test [path]",
            Category: "Test",
            Aliases: [],
            Arguments: [new CommandManifestArgument("[path-or-device ...]", "Target.", false, null)],
            Options: [],
            Examples: [], Notes: [], Output: null, PipelineInput: null);

        var latex = CommandLatexEmitter.Emit([entry]);
        // Should have {} guard before the bracket
        Assert.Contains("{}[", latex);
    }

    // ── Aliases ──────────────────────────────────────────────────────────

    [Fact]
    public void Emit_renders_aliases()
    {
        var entry = new CommandManifestEntry(
            Name: "test",
            Description: "Test.",
            Usage: "test",
            Category: "Test",
            Aliases: ["t", "tst"],
            Arguments: [], Options: [],
            Examples: [], Notes: [], Output: null, PipelineInput: null);

        var latex = CommandLatexEmitter.Emit([entry]);
        Assert.Contains("\\textit{Aliases:}", latex);
        Assert.Contains("\\code{t}", latex);
        Assert.Contains("\\code{tst}", latex);
    }

    // ── Full registry integration ────────────────────────────────────────

    [Fact]
    public void BuildManifest_with_real_registry_has_all_categories()
    {
        var runtime = ToshRuntime.CreateDefault();
        var manifest = CommandManifestExporter.BuildManifest(runtime.Commands);

        Assert.True(manifest.Count > 100, $"Expected >100 commands, got {manifest.Count}");

        var categories = manifest.Select(e => e.Category).Distinct().ToHashSet();
        Assert.Contains("Filesystem", categories);
        Assert.Contains("Text", categories);
        Assert.Contains("Pipeline", categories);
        Assert.Contains("System", categories);
        Assert.Contains("CLR", categories);
        Assert.Contains("Shell", categories);
        Assert.Contains("Prompt", categories);
        Assert.Contains("Functional", categories);
        Assert.Contains("Process", categories);
        Assert.Contains("Data", categories);
        Assert.Contains("Network", categories);
        Assert.Contains("Scripting", categories);
    }

    [Fact]
    public void BuildManifest_every_command_has_nonempty_category()
    {
        var runtime = ToshRuntime.CreateDefault();
        var manifest = CommandManifestExporter.BuildManifest(runtime.Commands);

        foreach (var entry in manifest)
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.Category),
                $"Command '{entry.Name}' has null/empty category.");
        }
    }

    [Fact]
    public void BuildManifest_every_command_has_nonempty_name_and_description()
    {
        var runtime = ToshRuntime.CreateDefault();
        var manifest = CommandManifestExporter.BuildManifest(runtime.Commands);

        foreach (var entry in manifest)
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.Name),
                "Found command with null/empty name.");
            Assert.False(string.IsNullOrWhiteSpace(entry.Description),
                $"Command '{entry.Name}' has null/empty description.");
        }
    }

    [Fact]
    public void Emit_full_manifest_produces_valid_latex()
    {
        var runtime = ToshRuntime.CreateDefault();
        var manifest = CommandManifestExporter.BuildManifest(runtime.Commands);
        var latex = CommandLatexEmitter.Emit(manifest);

        // Basic structural validation
        Assert.Contains("AUTO-GENERATED", latex);
        Assert.Contains("\\section{", latex);
        Assert.Contains("\\subsection{", latex);

        // Every cmdbox that opens must close
        var opens = latex.Split("\\begin{cmdbox}").Length - 1;
        var closes = latex.Split("\\end{cmdbox}").Length - 1;
        Assert.Equal(opens, closes);

        // Every lstlisting that opens must close
        var lstOpens = latex.Split("\\begin{lstlisting}").Length - 1;
        var lstCloses = latex.Split("\\end{lstlisting}").Length - 1;
        Assert.Equal(lstOpens, lstCloses);
    }

    [Fact]
    public void ExportJson_full_registry_roundtrips_to_latex()
    {
        var runtime = ToshRuntime.CreateDefault();
        var json = CommandManifestExporter.ExportJson(runtime.Commands);
        var latex = CommandLatexEmitter.EmitFromJson(json);

        Assert.Contains("\\section{", latex);
        Assert.Contains("\\subsection{", latex);
    }
}
