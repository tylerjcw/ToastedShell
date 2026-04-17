using Tosh.Core;

namespace Tosh.Tests;

/// <summary>
/// Tests the command introspection pipeline: attribute metadata extraction,
/// metadata export, and LaTeX emission.
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

    // ── BuildMetadata ────────────────────────────────────────────────────

    [Fact]
    public void BuildMetadata_extracts_category_from_attribute()
    {
        var registry = CreateRegistry(new FullyAnnotatedCommand());
        var metadata = CommandMetadataExporter.BuildMetadata(registry);

        var entry = Assert.Single(metadata);
        Assert.Equal("Testing", entry.Category);
    }

    [Fact]
    public void BuildMetadata_extracts_arguments_from_attributes()
    {
        var registry = CreateRegistry(new FullyAnnotatedCommand());
        var entry = Assert.Single(CommandMetadataExporter.BuildMetadata(registry));

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
    public void BuildMetadata_extracts_options_from_attributes()
    {
        var registry = CreateRegistry(new FullyAnnotatedCommand());
        var entry = Assert.Single(CommandMetadataExporter.BuildMetadata(registry));

        Assert.Equal(2, entry.Options.Count);
        Assert.Equal("-r, --recursive", entry.Options[0].Syntax);
        Assert.Equal("Process recursively.", entry.Options[0].Description);
        Assert.Equal("-v", entry.Options[1].Syntax);
    }

    [Fact]
    public void BuildMetadata_extracts_examples_from_attributes()
    {
        var registry = CreateRegistry(new FullyAnnotatedCommand());
        var entry = Assert.Single(CommandMetadataExporter.BuildMetadata(registry));

        Assert.Equal(2, entry.Examples.Count);
        Assert.Equal("test-cmd /tmp", entry.Examples[0].Code);
        Assert.Equal("Basic usage", entry.Examples[0].Title);
        Assert.Equal("test-cmd /tmp -r", entry.Examples[1].Code);
        Assert.Null(entry.Examples[1].Title);
    }

    [Fact]
    public void BuildMetadata_extracts_notes_from_attributes()
    {
        var registry = CreateRegistry(new FullyAnnotatedCommand());
        var entry = Assert.Single(CommandMetadataExporter.BuildMetadata(registry));

        Assert.Single(entry.Notes);
        Assert.Equal("This is a test note.", entry.Notes[0]);
    }

    [Fact]
    public void BuildMetadata_extracts_output_from_attribute()
    {
        var registry = CreateRegistry(new FullyAnnotatedCommand());
        var entry = Assert.Single(CommandMetadataExporter.BuildMetadata(registry));

        Assert.Equal("A list of processed items.", entry.Output);
    }

    [Fact]
    public void BuildMetadata_extracts_pipeline_input_from_attribute()
    {
        var registry = CreateRegistry(new FullyAnnotatedCommand());
        var entry = Assert.Single(CommandMetadataExporter.BuildMetadata(registry));

        Assert.NotNull(entry.PipelineInput);
        Assert.True(entry.PipelineInput.AcceptsScalar);
        Assert.True(entry.PipelineInput.AcceptsList);
        Assert.False(entry.PipelineInput.AcceptsRecord);
        Assert.False(entry.PipelineInput.AcceptsTable);
        Assert.Equal("Items to process.", entry.PipelineInput.Description);
    }

    [Fact]
    public void BuildMetadata_minimal_command_has_empty_collections()
    {
        var registry = CreateRegistry(new MinimalCommand());
        var entry = Assert.Single(CommandMetadataExporter.BuildMetadata(registry));

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
    public void BuildMetadata_preserves_name_description_usage()
    {
        var registry = CreateRegistry(new FullyAnnotatedCommand());
        var entry = Assert.Single(CommandMetadataExporter.BuildMetadata(registry));

        Assert.Equal("test-cmd", entry.Name);
        Assert.Equal("A test command.", entry.Description);
        Assert.Equal("test-cmd <path> [name] [-r] [-v]", entry.Usage);
    }

    [Fact]
    public void BuildMetadata_orders_by_category_then_name()
    {
        var registry = CreateRegistry(new MinimalCommand(), new FullyAnnotatedCommand());
        var metadata = CommandMetadataExporter.BuildMetadata(registry);

        Assert.Equal(2, metadata.Count);
        Assert.Equal("Minimal", metadata[0].Category);
        Assert.Equal("Testing", metadata[1].Category);
    }

    // ── ExportJson ───────────────────────────────────────────────────────

    [Fact]
    public void ExportJson_produces_valid_json_with_camelCase()
    {
        var registry = CreateRegistry(new FullyAnnotatedCommand());
        var json = CommandMetadataExporter.ExportMetadataJson(registry);

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
        var metadata = CommandMetadataExporter.BuildMetadata(registry);
        var latex = CommandLatexEmitter.Emit(metadata);

        Assert.Contains("\\section{Testing}", latex);
        Assert.Contains("\\section{Minimal}", latex);
    }

    [Fact]
    public void Emit_generates_subsection_per_command()
    {
        var registry = CreateRegistry(new FullyAnnotatedCommand());
        var metadata = CommandMetadataExporter.BuildMetadata(registry);
        var latex = CommandLatexEmitter.Emit(metadata);

        Assert.Contains("\\subsection{\\texttt{test-cmd}}", latex);
        Assert.Contains("\\label{ref:test-cmd}", latex);
        Assert.Contains("\\icmd{test-cmd}", latex);
    }

    [Fact]
    public void Emit_generates_cmdbox_with_description()
    {
        var registry = CreateRegistry(new FullyAnnotatedCommand());
        var metadata = CommandMetadataExporter.BuildMetadata(registry);
        var latex = CommandLatexEmitter.Emit(metadata);

        Assert.Contains("\\begin{cmdbox}{test-cmd}", latex);
        Assert.Contains("A test command.", latex);
        Assert.Contains("\\end{cmdbox}", latex);
    }

    [Fact]
    public void Emit_generates_signature()
    {
        var registry = CreateRegistry(new FullyAnnotatedCommand());
        var metadata = CommandMetadataExporter.BuildMetadata(registry);
        var latex = CommandLatexEmitter.Emit(metadata);

        Assert.Contains("\\begin{signature}", latex);
        Assert.Contains("\\end{signature}", latex);
    }

    [Fact]
    public void Emit_generates_arguments_table()
    {
        var registry = CreateRegistry(new FullyAnnotatedCommand());
        var metadata = CommandMetadataExporter.BuildMetadata(registry);
        var latex = CommandLatexEmitter.Emit(metadata);

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
        var metadata = CommandMetadataExporter.BuildMetadata(registry);
        var latex = CommandLatexEmitter.Emit(metadata);

        Assert.Contains("\\textbf{Flag / Option}", latex);
        Assert.Contains("-r, --recursive", latex);
        Assert.Contains("Process recursively.", latex);
    }

    [Fact]
    public void Emit_generates_examples_in_lstlisting()
    {
        var registry = CreateRegistry(new FullyAnnotatedCommand());
        var metadata = CommandMetadataExporter.BuildMetadata(registry);
        var latex = CommandLatexEmitter.Emit(metadata);

        Assert.Contains("\\begin{lstlisting}", latex);
        Assert.Contains("test-cmd /tmp  # Basic usage", latex);
        Assert.Contains("test-cmd /tmp -r", latex);
        Assert.Contains("\\end{lstlisting}", latex);
    }

    [Fact]
    public void Emit_generates_notes_in_notebox()
    {
        var registry = CreateRegistry(new FullyAnnotatedCommand());
        var metadata = CommandMetadataExporter.BuildMetadata(registry);
        var latex = CommandLatexEmitter.Emit(metadata);

        Assert.Contains("\\begin{notebox}", latex);
        Assert.Contains("This is a test note.", latex);
        Assert.Contains("\\end{notebox}", latex);
    }

    [Fact]
    public void Emit_generates_output_description()
    {
        var registry = CreateRegistry(new FullyAnnotatedCommand());
        var metadata = CommandMetadataExporter.BuildMetadata(registry);
        var latex = CommandLatexEmitter.Emit(metadata);

        Assert.Contains("\\textbf{Output:} A list of processed items.", latex);
    }

    [Fact]
    public void Emit_generates_pipeline_input_info()
    {
        var registry = CreateRegistry(new FullyAnnotatedCommand());
        var metadata = CommandMetadataExporter.BuildMetadata(registry);
        var latex = CommandLatexEmitter.Emit(metadata);

        Assert.Contains("\\textbf{Pipeline input:} scalar, list", latex);
        Assert.Contains("Items to process.", latex);
    }

    [Fact]
    public void Emit_skips_empty_sections_for_minimal_command()
    {
        var registry = CreateRegistry(new MinimalCommand());
        var metadata = CommandMetadataExporter.BuildMetadata(registry);
        var latex = CommandLatexEmitter.Emit(metadata);

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
        // Create a metadata entry whose description contains the special chars.
        var entry = new CommandMetadata(
            Name: "esc-test",
            Description: input,
            LongDescription: null,
            Usage: "esc-test",
            Category: "Test",
            Aliases: [],
            Arguments: [],
            Options: [],
            Examples: [],
            Notes: [],
            Output: null,
            PipelineInput: null,
            OutputType: null, OutputMembers: null, OutputMode: "structured", SideEffects: null, SinceVersion: null, DeprecatedVersion: null, RemovedVersion: null, Tags: [], SeeAlso: [], Permissions: [], IsExperimental: false, ErrorConditions: [], CanonicalExamples: []);

        var latex = CommandLatexEmitter.Emit([entry]);
        Assert.Contains(expected, latex);
    }

    // ── FormatUsage ──────────────────────────────────────────────────────

    [Fact]
    public void Emit_short_usage_stays_on_one_line()
    {
        var entry = new CommandMetadata(
            Name: "short",
            Description: "Short.",
            LongDescription: null,
            Usage: "short -a",
            Category: "Test",
            Aliases: [], Arguments: [], Options: [],
            Examples: [], Notes: [], Output: null, PipelineInput: null, OutputType: null, OutputMembers: null, OutputMode: "structured", SideEffects: null, SinceVersion: null, DeprecatedVersion: null, RemovedVersion: null, Tags: [], SeeAlso: [], Permissions: [], IsExperimental: false, ErrorConditions: [], CanonicalExamples: []);

        var latex = CommandLatexEmitter.Emit([entry]);
        // Should not contain continuation marker
        Assert.DoesNotContain("\\quad", latex);
    }

    [Fact]
    public void Emit_long_usage_wraps_with_continuation()
    {
        var longUsage = "find <path> [-name <pattern>] [-type <f|d>] [-maxdepth <n>] [-mindepth <n>] [-size <spec>] [-mtime <n>] [-newer <file>] [-perm <mode>] [-exec <command>]";
        var entry = new CommandMetadata(
            Name: "find",
            Description: "Find files.",
            LongDescription: null,
            Usage: longUsage,
            Category: "Test",
            Aliases: [], Arguments: [], Options: [],
            Examples: [], Notes: [], Output: null, PipelineInput: null, OutputType: null, OutputMembers: null, OutputMode: "structured", SideEffects: null, SinceVersion: null, DeprecatedVersion: null, RemovedVersion: null, Tags: [], SeeAlso: [], Permissions: [], IsExperimental: false, ErrorConditions: [], CanonicalExamples: []);

        var latex = CommandLatexEmitter.Emit([entry]);
        Assert.Contains("\\quad", latex);
    }

    [Fact]
    public void Emit_pipe_delimited_tokens_get_allowbreak()
    {
        // Token >28 chars with pipes should get \allowbreak — usage must exceed 72 chars to enter the word-splitting branch
        var usage = "http <get|post|put|patch|delete|head|options> <url> [-H <header>] [-d <body>] [--timeout <ms>]";
        var entry = new CommandMetadata(
            Name: "http",
            Description: "HTTP.",
            LongDescription: null,
            Usage: usage,
            Category: "Test",
            Aliases: [], Arguments: [], Options: [],
            Examples: [], Notes: [], Output: null, PipelineInput: null, OutputType: null, OutputMembers: null, OutputMode: "structured", SideEffects: null, SinceVersion: null, DeprecatedVersion: null, RemovedVersion: null, Tags: [], SeeAlso: [], Permissions: [], IsExperimental: false, ErrorConditions: [], CanonicalExamples: []);

        var latex = CommandLatexEmitter.Emit([entry]);
        Assert.Contains("\\allowbreak", latex);
    }

    // ── EmitFromJson round-trip ──────────────────────────────────────────

    [Fact]
    public void EmitFromJson_roundtrips_through_json()
    {
        var registry = CreateRegistry(new FullyAnnotatedCommand());
        var json = CommandMetadataExporter.ExportMetadataJson(registry);
        var latex = CommandLatexEmitter.EmitFromJson(json);

        Assert.Contains("\\section{Testing}", latex);
        Assert.Contains("\\subsection{\\texttt{test-cmd}}", latex);
        Assert.Contains("A test command.", latex);
    }

    // ── Bracket-at-cell-start guard ──────────────────────────────────────

    [Fact]
    public void Emit_argument_starting_with_bracket_gets_guard()
    {
        var entry = new CommandMetadata(
            Name: "test",
            Description: "Test.",
            LongDescription: null,
            Usage: "test [path]",
            Category: "Test",
            Aliases: [],
            Arguments: [new CommandArgumentMetadata("[path-or-device ...]", "Target.", false, null, null)],
            Options: [],
            Examples: [], Notes: [], Output: null, PipelineInput: null, OutputType: null, OutputMembers: null, OutputMode: "structured", SideEffects: null, SinceVersion: null, DeprecatedVersion: null, RemovedVersion: null, Tags: [], SeeAlso: [], Permissions: [], IsExperimental: false, ErrorConditions: [], CanonicalExamples: []);

        var latex = CommandLatexEmitter.Emit([entry]);
        // Should have {} guard before the bracket
        Assert.Contains("{}[", latex);
    }

    // ── Aliases ──────────────────────────────────────────────────────────

    [Fact]
    public void Emit_renders_aliases()
    {
        var entry = new CommandMetadata(
            Name: "test",
            Description: "Test.",
            LongDescription: null,
            Usage: "test",
            Category: "Test",
            Aliases: ["t", "tst"],
            Arguments: [], Options: [],
            Examples: [], Notes: [], Output: null, PipelineInput: null, OutputType: null, OutputMembers: null, OutputMode: "structured", SideEffects: null, SinceVersion: null, DeprecatedVersion: null, RemovedVersion: null, Tags: [], SeeAlso: [], Permissions: [], IsExperimental: false, ErrorConditions: [], CanonicalExamples: []);

        var latex = CommandLatexEmitter.Emit([entry]);
        Assert.Contains("\\textit{Aliases:}", latex);
        Assert.Contains("\\code{t}", latex);
        Assert.Contains("\\code{tst}", latex);
    }

    // ── Full registry integration ────────────────────────────────────────

    [Fact]
    public void BuildMetadata_with_real_registry_has_all_categories()
    {
        var runtime = ToshRuntime.CreateDefault();
        var metadata = CommandMetadataExporter.BuildMetadata(runtime.Commands);

        Assert.True(metadata.Count > 100, $"Expected >100 commands, got {metadata.Count}");

        var categories = metadata.Select(e => e.Category).Distinct().ToHashSet();
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
    public void BuildMetadata_every_command_has_nonempty_category()
    {
        var runtime = ToshRuntime.CreateDefault();
        var metadata = CommandMetadataExporter.BuildMetadata(runtime.Commands);

        foreach (var entry in metadata)
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.Category),
                $"Command '{entry.Name}' has null/empty category.");
        }
    }

    [Fact]
    public void BuildMetadata_every_command_has_nonempty_name_and_description()
    {
        var runtime = ToshRuntime.CreateDefault();
        var metadata = CommandMetadataExporter.BuildMetadata(runtime.Commands);

        foreach (var entry in metadata)
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.Name),
                "Found command with null/empty name.");
            Assert.False(string.IsNullOrWhiteSpace(entry.Description),
                $"Command '{entry.Name}' has null/empty description.");
        }
    }

    [Fact]
    public void Emit_full_metadata_produces_valid_latex()
    {
        var runtime = ToshRuntime.CreateDefault();
        var metadata = CommandMetadataExporter.BuildMetadata(runtime.Commands);
        var latex = CommandLatexEmitter.Emit(metadata);

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
        var json = CommandMetadataExporter.ExportMetadataJson(runtime.Commands);
        var latex = CommandLatexEmitter.EmitFromJson(json);

        Assert.Contains("\\section{", latex);
        Assert.Contains("\\subsection{", latex);
    }

    // ── GetMetadata ──────────────────────────────────────────────────────

    [Fact]
    public void GetMetadata_returns_metadata_from_attributes()
    {
        var cmd = new FullyAnnotatedCommand();
        var meta = cmd.GetMetadata();

        Assert.Equal("test-cmd", meta.Name);
        Assert.Equal("A test command.", meta.Description);
        Assert.Equal("Testing", meta.Category);
        Assert.Equal(2, meta.Arguments.Count);
        Assert.Equal(2, meta.Options.Count);
        Assert.Equal(2, meta.Examples.Count);
        Assert.Single(meta.Notes);
        Assert.Equal("A list of processed items.", meta.Output);
        Assert.NotNull(meta.PipelineInput);
    }

    [Fact]
    public void GetMetadata_passes_through_aliases()
    {
        var cmd = new FullyAnnotatedCommand();
        var meta = cmd.GetMetadata(["tc", "tcmd"]);

        Assert.Equal(2, meta.Aliases.Count);
        Assert.Contains("tc", meta.Aliases);
        Assert.Contains("tcmd", meta.Aliases);
    }

    [Fact]
    public void GetMetadata_minimal_command_has_empty_collections()
    {
        var cmd = new MinimalCommand();
        var meta = cmd.GetMetadata();

        Assert.Equal("Minimal", meta.Category);
        Assert.Empty(meta.Arguments);
        Assert.Empty(meta.Options);
        Assert.Empty(meta.Examples);
        Assert.Empty(meta.Notes);
        Assert.Null(meta.Output);
        Assert.Null(meta.PipelineInput);
    }

    [Fact]
    public void GetMetadata_matches_exporter_output()
    {
        var cmd = new FullyAnnotatedCommand();
        var registry = CreateRegistry(cmd);
        var exported = CommandMetadataExporter.BuildMetadata(registry);
        var direct = cmd.GetMetadata();

        var entry = Assert.Single(exported);
        Assert.Equal(direct.Name, entry.Name);
        Assert.Equal(direct.Category, entry.Category);
        Assert.Equal(direct.Arguments.Count, entry.Arguments.Count);
        Assert.Equal(direct.Options.Count, entry.Options.Count);
        Assert.Equal(direct.Output, entry.Output);
    }

    // ── CommandAlias attribute ────────────────────────────────────────────

    [CommandCategory("Testing")]
    [CommandAlias("primary-cmd")]
    private sealed class AliasCommand : ShellCommand
    {
        public AliasCommand()
            : base("pc", "Alias for primary-cmd.", "pc [args]") { }

        public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    [CommandCategory("Testing")]
    private sealed class PrimaryCommand : ShellCommand
    {
        public PrimaryCommand()
            : base("primary-cmd", "The primary command.", "primary-cmd [args]") { }

        public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    [Fact]
    public void BuildMetadata_explicit_alias_appears_in_primary_aliases()
    {
        var registry = CreateRegistry(new PrimaryCommand(), new AliasCommand());
        var metadata = CommandMetadataExporter.BuildMetadata(registry);

        var entry = Assert.Single(metadata);
        Assert.Equal("primary-cmd", entry.Name);
        Assert.Contains("pc", entry.Aliases);
    }

    [Fact]
    public void BuildMetadata_explicit_alias_is_not_separate_entry()
    {
        var registry = CreateRegistry(new PrimaryCommand(), new AliasCommand());
        var metadata = CommandMetadataExporter.BuildMetadata(registry);

        Assert.Single(metadata);
        Assert.DoesNotContain(metadata, e => e.Name == "pc");
    }

    [Fact]
    public void CommandAlias_attribute_has_correct_canonical_name()
    {
        var attr = (CommandAliasAttribute)typeof(AliasCommand)
            .GetCustomAttributes(typeof(CommandAliasAttribute), false)
            .Single();

        Assert.Equal("primary-cmd", attr.CanonicalName);
    }

    // ── GetMetadata on real commands ─────────────────────────────────────

    [Fact]
    public void GetMetadata_on_real_where_command_has_attributes()
    {
        var cmd = new Tosh.Core.Commands.WhereCommand();
        var meta = cmd.GetMetadata();

        Assert.Equal("where", meta.Name);
        Assert.Equal("Pipeline", meta.Category);
        Assert.Single(meta.Arguments);
        Assert.Equal("predicate", meta.Arguments[0].Name);
        Assert.NotEmpty(meta.Examples);
        Assert.NotNull(meta.PipelineInput);
        Assert.Equal("Pipeline objects for which the predicate returned true.", meta.Output);
    }

    [Fact]
    public void GetMetadata_on_real_sort_command_has_options()
    {
        var cmd = new Tosh.Core.Commands.SortCommand();
        var meta = cmd.GetMetadata();

        Assert.Equal("sort", meta.Name);
        Assert.Equal("Pipeline", meta.Category);
        Assert.NotEmpty(meta.Options);
    }

    [Fact]
    public void GetMetadata_consistency_every_real_command_matches_exporter()
    {
        var runtime = ToshRuntime.CreateDefault();
        var exported = CommandMetadataExporter.BuildMetadata(runtime.Commands)
            .ToDictionary(e => e.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var command in runtime.Commands.All)
        {
            if (command is not ShellCommand shellCommand) continue;

            // Aliases are not separate entries in exported metadata.
            if (!exported.TryGetValue(command.Name, out var entry)) continue;

            var direct = shellCommand.GetMetadata();
            Assert.Equal(entry.Category, direct.Category);
            Assert.Equal(entry.Arguments.Count, direct.Arguments.Count);
            Assert.Equal(entry.Options.Count, direct.Options.Count);
            Assert.Equal(entry.Output, direct.Output);
        }
    }

    // ── VsCode metadata emitter ────────────────────────────────────────

    [Fact]
    public void VsCodeEmit_produces_valid_json_with_three_sections()
    {
        var registry = CreateRegistry(new FullyAnnotatedCommand());
        var metadata = CommandMetadataExporter.BuildMetadata(registry);
        var json = VsCodeMetadataEmitter.Emit(metadata);

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("keywords", out _));
        Assert.True(root.TryGetProperty("specialVariables", out _));
        Assert.True(root.TryGetProperty("builtins", out _));

        Assert.True(root.GetProperty("builtins").TryGetProperty("test-cmd", out var desc));
        Assert.Equal("A test command.", desc.GetString());
    }

    [Fact]
    public void VsCodeEmit_includes_aliases_as_separate_entries()
    {
        var registry = CreateRegistry(new PrimaryCommand(), new AliasCommand());
        var metadata = CommandMetadataExporter.BuildMetadata(registry);
        var json = VsCodeMetadataEmitter.Emit(metadata);

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var builtins = doc.RootElement.GetProperty("builtins");

        Assert.True(builtins.TryGetProperty("primary-cmd", out _));
        Assert.True(builtins.TryGetProperty("pc", out var aliasDesc));
        Assert.Equal("Alias for `primary-cmd`.", aliasDesc.GetString());
    }

    [Fact]
    public void VsCodeEmit_real_registry_covers_all_registered_builtins()
    {
        var runtime = ToshRuntime.CreateDefault();
        var metadata = CommandMetadataExporter.BuildMetadata(runtime.Commands);
        var json = VsCodeMetadataEmitter.Emit(metadata);

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var builtins = doc.RootElement.GetProperty("builtins");
        var builtinNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in builtins.EnumerateObject())
            builtinNames.Add(prop.Name);

        foreach (var command in runtime.Commands.All)
        {
            if (command is Tosh.Core.Commands.ExternalProcessCommand) continue;

            Assert.True(builtinNames.Contains(command.Name),
                $"Registered builtin '{command.Name}' is missing from generated VS Code metadata.");
        }
    }

    [Fact]
    public void VsCodeEmit_builtins_have_nonempty_descriptions()
    {
        var runtime = ToshRuntime.CreateDefault();
        var metadata = CommandMetadataExporter.BuildMetadata(runtime.Commands);
        var json = VsCodeMetadataEmitter.Emit(metadata);

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var builtins = doc.RootElement.GetProperty("builtins");

        foreach (var prop in builtins.EnumerateObject())
        {
            Assert.False(string.IsNullOrWhiteSpace(prop.Value.GetString()),
                $"VS Code builtin '{prop.Name}' has null/empty description.");
        }
    }

    // ── Phase 4: Strict validation ──────────────────────────────────────

    [Fact]
    public void BuildMetadata_no_duplicate_canonical_names()
    {
        var runtime = ToshRuntime.CreateDefault();
        var metadata = CommandMetadataExporter.BuildMetadata(runtime.Commands);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in metadata)
        {
            Assert.True(seen.Add(entry.Name),
                $"Duplicate canonical name: '{entry.Name}'.");
        }
    }

    [Fact]
    public void BuildMetadata_alias_names_do_not_collide_with_canonical_names()
    {
        var runtime = ToshRuntime.CreateDefault();
        var metadata = CommandMetadataExporter.BuildMetadata(runtime.Commands);

        var canonicalNames = metadata.Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in metadata)
        {
            foreach (var alias in entry.Aliases)
            {
                Assert.False(canonicalNames.Contains(alias),
                    $"Alias '{alias}' on command '{entry.Name}' collides with a canonical command name.");
            }
        }
    }

    [Fact]
    public void BuildMetadata_every_alias_resolves_to_registered_command()
    {
        var runtime = ToshRuntime.CreateDefault();
        var metadata = CommandMetadataExporter.BuildMetadata(runtime.Commands);
        var registeredNames = runtime.Commands.All.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in metadata)
        {
            foreach (var alias in entry.Aliases)
            {
                Assert.True(registeredNames.Contains(alias),
                    $"Alias '{alias}' on command '{entry.Name}' is not a registered command.");
            }
        }
    }

    [Fact]
    public void Emit_latex_contains_every_builtin_command()
    {
        var runtime = ToshRuntime.CreateDefault();
        var metadata = CommandMetadataExporter.BuildMetadata(runtime.Commands);
        var latex = CommandLatexEmitter.Emit(metadata);

        foreach (var entry in metadata)
        {
            Assert.Contains($"\\subsection{{\\texttt{{{entry.Name}}}", latex);
        }
    }

    [Fact]
    public void BuildMetadata_manifest_is_deterministic()
    {
        var runtime = ToshRuntime.CreateDefault();
        var json1 = CommandMetadataExporter.ExportMetadataJson(runtime.Commands);
        var json2 = CommandMetadataExporter.ExportMetadataJson(runtime.Commands);

        Assert.Equal(json1, json2);
    }

    [Fact]
    public void HelpTopics_builtin_commands_resolve_from_metadata_not_catalog()
    {
        var runtime = ToshRuntime.CreateDefault();

        foreach (var command in runtime.Commands.All)
        {
            if (command is Tosh.Core.Commands.ExternalProcessCommand) continue;
            if (command is not ShellCommand shellCommand) continue;

            // Skip aliases — they share topics with their canonical command.
            if (shellCommand.GetType().GetCustomAttributes(typeof(CommandAliasAttribute), false).Length > 0)
                continue;

            var topic = HelpCatalog.ResolveTopic(runtime, command.Name);
            Assert.NotNull(topic);

            // Language/type topics intentionally override command topics of the same name.
            if (topic.Kind is not HelpSubjectKind.BuiltIn) continue;

            var meta = shellCommand.GetMetadata();
            Assert.Equal(meta.Category, topic.Category);
            Assert.Equal(meta.Description, topic.Description);
        }
    }
}
