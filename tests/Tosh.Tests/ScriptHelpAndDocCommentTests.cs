using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// What a script and a function say about themselves when asked — the <c>--help</c> and
/// doc-comment surfaces.
/// </summary>
/// <remarks>
/// <para>
/// Reported from use: "arg doc comments do not appear in the built in --help system". Reviewing
/// that path turned up four separate defects, each of which loses authored documentation without
/// saying so.
/// </para>
/// <list type="bullet">
/// <item>
/// <b>A script had no <c>--help</c> at all.</b> One declaring <c>arg</c>/<c>flag</c> inputs
/// rejected <c>--help</c> as <c>tosh.runtime.unknown_script_flag</c> — the flag reached the
/// ordinary lookup and missed. The descriptions written above each declaration were parsed, stored
/// on the parameter, and had nowhere to go. The parser's own comment says the file-level doc-block
/// is "surfaced in <c>--help</c>", which was not true of anything.
/// </item>
/// <item>
/// <b><c>@param name desc</c> was swallowed into the description.</b> The specification teaches
/// <c>@param=&lt;name&gt;</c> in one section and <c>@param &lt;name&gt;</c> in another; only the
/// first was understood, so the second became part of the summary text — "Adds two numbers.
/// @param a first value" — with no parameter documentation produced and no diagnostic.
/// </item>
/// <item>
/// <b>A trailing <c>@example</c> block was lost.</b> The flush that ends an example block at the
/// end of a doc-comment sat inside the token loop, where it could never run. The same block
/// followed by any other tag survived, which is what made it look like it worked.
/// </item>
/// <item>
/// <b>Documented functions got no structured help.</b> Parameters were pre-rendered into the
/// <c>Notes</c> string with embedded newlines, which the panel could not split into rows, so the
/// text spilled outside the box border. <c>@deprecated</c>, <c>@since</c>, <c>@throws</c> and
/// <c>@see</c> were parsed and then dropped entirely.
/// </item>
/// </list>
/// </remarks>
public sealed class ScriptHelpAndDocCommentTests
{
    /// <summary>Runs <paramref name="script"/> as a file and returns what it wrote.</summary>
    private static async Task<string> RunScriptAsync(string script, params string[] arguments)
    {
        var path = Path.Combine(Path.GetTempPath(), $"tosh-help-{Guid.NewGuid():N}.tosh");
        await File.WriteAllTextAsync(path, script);

        try
        {
            var writer = new StringWriter();
            var runtime = ToshRuntime.CreateDefault();
            runtime.Output = writer;

            var engine = new ToshEngine(runtime);

            // Answering `--help` asks the runtime to exit rather than throwing: the script simply
            // stops before its body, the way `exit` does.
            var values = await AsyncEnumerableExtensions.ToListAsync(
                engine.ExecuteScriptFileAsync(path, arguments),
                default);

            // Usage is *written*, while the script's own output comes back as pipeline values —
            // the host renders those through a display sink. Both are the script speaking, so
            // both count here; reading only the writer made every case that actually ran look
            // silent.
            // Subcommand help is a HelpTopic value rather than written text — the same object
            // `help <name>` produces, rendered by the same panel renderer. Rendering it here is
            // what lets these assertions read the output a user actually sees.
            return string.Join(
                "\n",
                new[] { writer.ToString() }
                    .Concat(values.Select(value => value switch
                    {
                        HelpTopic topic => HelpTopicSummaryRenderer.Render(topic),
                        null => string.Empty,
                        _ => value.ToString() ?? string.Empty,
                    })));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static async Task<HelpTopic> TopicForAsync(string source, string name)
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);
        await engine.ExecuteToListAsync(source);

        var topic = HelpCatalog.ResolveTopic(runtime, name);
        Assert.NotNull(topic);
        return topic!;
    }

    private const string Greet =
        """
        ## Greets a person by name.

        ## Who to greet.
        arg name: string

        ## Shout the greeting.
        flag loud: bool = false

        echo $"Hello, {$name}"
        """;

    // ── A script describes itself ──────────────────────────────────────────────

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    public async Task A_script_answers_the_help_flag(string flag)
    {
        var output = await RunScriptAsync(Greet, flag);

        Assert.Contains("Usage:", output, StringComparison.Ordinal);
        Assert.DoesNotContain("unknown_script_flag", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_usage_carries_the_descriptions_that_were_written()
    {
        // The reported defect: these are the doc-comments above each declaration, and the whole
        // reason someone writes them.
        var output = await RunScriptAsync(Greet, "--help");

        Assert.Contains("Who to greet.", output, StringComparison.Ordinal);
        Assert.Contains("Shout the greeting.", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_summary_comes_from_the_file_level_doc_block()
    {
        // Not from the first declaration's block, which is a different comment about a different
        // thing — taking it printed "Who to greet." as the summary of a script that greets people.
        var output = await RunScriptAsync(Greet, "--help");

        Assert.Contains("Greets a person by name.", output, StringComparison.Ordinal);
        Assert.StartsWith("Greets a person by name.", output.TrimStart(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Arguments_and_options_are_listed_separately()
    {
        var output = await RunScriptAsync(Greet, "--help");

        Assert.Contains("Arguments:", output, StringComparison.Ordinal);
        Assert.Contains("Options:", output, StringComparison.Ordinal);
        Assert.Contains("--loud", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Required_optional_and_rest_arguments_are_spelled_differently()
    {
        var output = await RunScriptAsync(
            """
            ## Demo.

            ## Required one.
            arg first: string

            ## Optional one.
            arg second: string = "x"

            ## The rest.
            arg others...: string
            """,
            "--help");

        Assert.Contains("<first>", output, StringComparison.Ordinal);
        Assert.Contains("[second]", output, StringComparison.Ordinal);
        Assert.Contains("[others...]", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_script_that_declares_its_own_help_flag_keeps_it()
    {
        // The built-in answer is a default for scripts that have not spoken, never an override of
        // one that has.
        var output = await RunScriptAsync(
            """
            ## Does a thing.

            ## Print custom help.
            flag help: bool = false

            if ($help) { echo "MY OWN HELP" }
            """,
            "--help");

        Assert.Contains("MY OWN HELP", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Usage:", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task After_a_bare_separator_the_flag_is_data()
    {
        var output = await RunScriptAsync(
            """
            ## Echoes.

            ## Items.
            arg items...: string

            echo $"got: {$items}"
            """,
            "--", "--help");

        Assert.Contains("got: --help", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Usage:", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_script_still_runs_when_help_is_not_asked_for()
    {
        Assert.Contains("Hello, Ada", await RunScriptAsync(Greet, "Ada"), StringComparison.Ordinal);
    }

    // ── `@param`, both spellings ───────────────────────────────────────────────

    [Theory]
    // The specification teaches both, in two different sections. Only one was understood.
    [InlineData("## @param a The first operand.")]
    [InlineData("## @param=a The first operand.")]
    public async Task Either_param_spelling_documents_the_parameter(string tag)
    {
        var topic = await TopicForAsync(
            $"## Adds two numbers.\n{tag}\nfunc add(a, b) {{ return ($a + $b) }}",
            "add");

        Assert.NotNull(topic.Arguments);
        var argument = Assert.Single(topic.Arguments!);
        Assert.Equal("a", argument.Name);
        Assert.Equal("The first operand.", argument.Description);
    }

    [Fact]
    public async Task The_space_spelling_no_longer_leaks_into_the_description()
    {
        // What made it silent: the tag text became part of the summary rather than being dropped,
        // so the documentation was visible in the wrong place and looked like a formatting slip.
        var topic = await TopicForAsync(
            "## Adds two numbers.\n## @param a first value\nfunc add(a, b) { return 1 }",
            "add");

        Assert.Equal("Adds two numbers.", topic.Description);
        Assert.DoesNotContain("@param", topic.Description, StringComparison.Ordinal);
    }

    // ── `@example` at the end of a doc-comment ─────────────────────────────────

    [Fact]
    public async Task A_trailing_example_block_survives()
    {
        var topic = await TopicForAsync(
            "## Adds.\n## @example\n##   add 3 4\nfunc add(a, b) { return 1 }",
            "add");

        Assert.Equal("add 3 4", Assert.Single(topic.Examples));
    }

    [Fact]
    public async Task A_trailing_example_block_keeps_every_line()
    {
        var topic = await TopicForAsync(
            "## Adds.\n## @example\n##   add 3 4\n##   add 5 6\nfunc add(a, b) { return 1 }",
            "add");

        Assert.Equal("add 3 4\nadd 5 6", Assert.Single(topic.Examples));
    }

    [Theory]
    // The controls: the spellings that already worked, and are what made the trailing case look
    // like it worked too.
    [InlineData("## Adds.\n## @example\n##   add 3 4\n## @since 1.0\nfunc add(a, b) { return 1 }")]
    [InlineData("## Adds.\n## @example add 3 4\nfunc add(a, b) { return 1 }")]
    public async Task Example_forms_that_already_worked_are_unchanged(string source)
    {
        var topic = await TopicForAsync(source, "add");

        Assert.Equal("add 3 4", Assert.Single(topic.Examples));
    }

    [Fact]
    public async Task An_empty_example_entry_is_not_produced()
    {
        // A blank leading entry rendered as a bullet with nothing beside it.
        var topic = await TopicForAsync(
            "## Adds.\n## @example\n##   add 3 4\nfunc add(a, b) { return 1 }",
            "add");

        Assert.DoesNotContain(topic.Examples, string.IsNullOrWhiteSpace);
    }

    // ── Documented functions get structured help ───────────────────────────────

    private const string FullyDocumented =
        """
        ## Does a thing.
        ## @param a An operand.
        ## @returns The result.
        ## @deprecated Use thing2 instead.
        ## @see thing2
        ## @since 1.2.0
        ## @throws IOError When it fails.
        func thing(a) { return $a }
        """;

    [Fact]
    public async Task Parameters_reach_the_structured_argument_list()
    {
        // Previously pre-rendered into `Notes` as one string with embedded newlines, which the
        // panel could not lay out — the text escaped the box border — and which left
        // `help thing | to json` reporting no arguments for a function that documented one.
        var topic = await TopicForAsync(FullyDocumented, "thing");

        var argument = Assert.Single(topic.Arguments!);
        Assert.Equal("a", argument.Name);
        Assert.Equal("An operand.", argument.Description);
    }

    [Fact]
    public async Task The_return_description_is_carried()
    {
        Assert.Equal("The result.", (await TopicForAsync(FullyDocumented, "thing")).Output);
    }

    [Theory]
    // Tags that were parsed and then dropped on the way to the help topic: a function could
    // declare itself deprecated and `help` would never say so.
    [InlineData("Use thing2 instead.")]
    [InlineData("1.2.0")]
    [InlineData("IOError")]
    public async Task Deprecation_since_and_throws_are_reported(string expected)
    {
        var topic = await TopicForAsync(FullyDocumented, "thing");

        Assert.NotNull(topic.Notes);
        Assert.Contains(expected, topic.Notes!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_see_reference_leads_the_related_list()
    {
        var topic = await TopicForAsync(FullyDocumented, "thing");

        Assert.Contains("thing2", topic.Related, StringComparer.Ordinal);
    }

    // ── Subcommands document their inputs ──────────────────────────────────────

    private const string Subcommands =
        """
        ## @summary Test harness.

        ## @summary Builds things.
        ## @arg=target What to build.
        ## @flag=fast Skip slow steps.
        subcommand build {
            arg target: string = "all"
            flag fast: bool = false
            echo "built"
        }
        """;

    [Theory]
    // The reported defect: the usage line named the positional arguments and nothing described
    // them, so a subcommand's help listed only `--help`.
    [InlineData("Arguments")]
    [InlineData("What to build.")]
    [InlineData("Skip slow steps.")]
    public async Task A_subcommand_help_describes_its_inputs(string expected)
    {
        Assert.Contains(expected, await RunScriptAsync(Subcommands, "build", "--help"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_subcommand_block_tag_wins_over_the_declarations_own_comment()
    {
        // Both places may document an input; the subcommand's own block is the one that decides,
        // so a single header can describe everything a subcommand takes.
        var output = await RunScriptAsync(
            """
            ## @summary Test harness.

            ## @summary Builds things.
            ## @arg=target From the block.
            subcommand build {
                ## @summary From the declaration.
                arg target: string = "all"
                echo "built"
            }
            """,
            "build",
            "--help");

        Assert.Contains("From the block.", output, StringComparison.Ordinal);
        Assert.DoesNotContain("From the declaration.", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_declaration_keeps_its_description_when_the_block_is_silent()
    {
        // The other half of the precedence rule: overriding is per input, not wholesale, which is
        // what lets a block describe one argument and leave the rest to their declarations.
        var output = await RunScriptAsync(
            """
            ## @summary Test harness.

            ## @summary Builds things.
            ## @arg=target From the block.
            subcommand build {
                ## @summary From the declaration.
                arg target: string = "all"

                ## Only the declaration mentions this one.
                flag fast: bool = false
                echo "built"
            }
            """,
            "build",
            "--help");

        Assert.Contains("Only the declaration mentions this one.", output, StringComparison.Ordinal);
    }

    [Theory]
    // `@arg` and `@flag` are the spellings that match the keywords a script declares its inputs
    // with; `@param` keeps working, and every one of them accepts either separator.
    [InlineData("## @arg=target Described.")]
    [InlineData("## @arg target Described.")]
    [InlineData("## @flag=target Described.")]
    [InlineData("## @param=target Described.")]
    [InlineData("## @param target Described.")]
    public async Task Every_named_input_tag_and_separator_documents_the_input(string tag)
    {
        var output = await RunScriptAsync(
            $$"""
            ## @summary Test harness.

            ## @summary Builds things.
            {{tag}}
            subcommand build {
                arg target: string = "all"
                echo "built"
            }
            """,
            "build",
            "--help");

        Assert.Contains("Described.", output, StringComparison.Ordinal);
    }
}
