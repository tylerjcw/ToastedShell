using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// <c>@arg</c> / <c>@flag</c> tags describe the inputs they name — <c>TS-P2-67</c>.
/// </summary>
/// <remarks>
/// <para>
/// Found by running the examples while writing a session summary rather than restating them. The
/// tags worked in exactly one placement and one separator; everywhere else the doc-comment's
/// <em>summary</em> was used as the description of every input, so a script documenting three
/// arguments showed the same sentence three times.
/// </para>
/// <para>
/// Three causes, not one. A comment is attached to the declaration that follows it, so a single
/// block documenting several inputs reached only the first — <c>## @flag clean …</c> written above
/// <c>arg target</c> described nothing, because the tag lived on the argument's comment while the
/// flag was a separate statement. A subcommand-free script never applied its own tags at all. And
/// the name ended at the first space and nothing else, so <c>@arg name=description</c> read
/// <c>name=description</c>'s first word as the name and <c>@arg name - description</c> kept the
/// hyphen in the text.
/// </para>
/// </remarks>
public sealed class DocCommentInputTagTests
{
    private static async Task<string> HelpForAsync(string script, params string[] arguments)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"tosh-doc-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            var path = Path.Combine(directory, "main.tosh");
            await File.WriteAllTextAsync(path, script);

            var runtime = ToshRuntime.CreateDefault();
            runtime.CurrentDirectory = directory;
            var output = new StringWriter();
            runtime.Output = output;

            var engine = new ToshEngine(runtime);
            var results = new List<object?>();

            await foreach (var value in engine.ExecuteScriptFileAsync(path, arguments))
            {
                results.Add(value);
            }

            // A subcommand answers `--help` with a HelpTopic *value*, which the CLI renders through
            // the display engine; a plain script writes its usage straight to the output. Both
            // shapes have to be reduced to text or half these assertions would test nothing.
            var rendered = results.Count > 0
                ? runtime.Display.RenderMany(results)
                : string.Empty;

            return output.ToString() + rendered;
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // ── Every separator means the same thing ───────────────────────────────────

    [Theory]
    [InlineData("@arg=target what to build")]
    [InlineData("@arg target what to build")]
    [InlineData("@arg target=what to build")]
    [InlineData("@arg target - what to build")]
    public async Task Any_separator_between_name_and_description_works(string tag)
    {
        var help = await HelpForAsync(
            $"""
            ## Builds the project.
            ## {tag}
            arg target: string
            """,
            "--help");

        Assert.Contains("what to build", help, StringComparison.Ordinal);
        Assert.DoesNotContain("- what to build", help, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_description_may_still_begin_with_a_hyphen()
    {
        // The hyphen is a separator only when whitespace follows it, so `-v` survives.
        var help = await HelpForAsync(
            """
            ## Runs it.
            ## @flag verbose - pass -v to the compiler
            flag verbose
            """,
            "--help");

        Assert.Contains("pass -v to the compiler", help, StringComparison.Ordinal);
    }

    // ── Every placement reaches the input it names ─────────────────────────────

    [Fact]
    public async Task A_subcommand_free_script_applies_its_own_tags()
    {
        var help = await HelpForAsync(
            """
            ## Builds the project.
            ## @arg target - what to build
            ## @flag clean - remove artefacts first
            arg target: string
            flag clean
            """,
            "--help");

        Assert.Contains("what to build", help, StringComparison.Ordinal);
        Assert.Contains("remove artefacts first", help, StringComparison.Ordinal);
    }

    [Fact]
    public async Task One_comment_describes_every_input_in_the_block()
    {
        // The comment attaches to the declaration that follows it, so its tags have to reach the
        // block's other declarations — which is how anyone actually writes one.
        var help = await HelpForAsync(
            """
            subcommand build {
                ## Builds the project.
                ## @arg target - what to build
                ## @flag clean - remove artefacts first
                arg target: string
                flag clean
            }
            """,
            "build", "--help");

        Assert.Contains("what to build", help, StringComparison.Ordinal);
        Assert.Contains("remove artefacts first", help, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tags_above_the_subcommand_block_still_work()
    {
        var help = await HelpForAsync(
            """
            ## Builds the project.
            ## @arg target - what to build
            subcommand build {
                arg target: string
            }
            """,
            "build", "--help");

        Assert.Contains("what to build", help, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_block_level_tag_wins_over_a_per_declaration_one()
    {
        // The precedence decided when the tags were designed, now actually exercised.
        var help = await HelpForAsync(
            """
            ## Builds the project.
            ## @arg target - the block-level text
            subcommand build {
                ## @arg target - the per-declaration text
                arg target: string
            }
            """,
            "build", "--help");

        Assert.Contains("the block-level text", help, StringComparison.Ordinal);
        Assert.DoesNotContain("the per-declaration text", help, StringComparison.Ordinal);
    }

    // ── Nothing that already worked changed ────────────────────────────────────

    [Fact]
    public async Task A_summary_still_describes_an_input_that_has_no_tag()
    {
        // The fallback: prose above a single declaration describes it, as before.
        var help = await HelpForAsync(
            """
            ## Where to write the output.
            arg destination: string
            """,
            "--help");

        Assert.Contains("Where to write the output.", help, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_script_summary_is_still_its_own()
    {
        var help = await HelpForAsync(
            """
            ## Builds the project.

            ## Where to write the output.
            arg destination: string
            """,
            "--help");

        Assert.Contains("Builds the project.", help, StringComparison.Ordinal);
        Assert.Contains("Where to write the output.", help, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_function_param_tag_is_unchanged()
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);

        await engine.ExecuteToListAsync(
            """
            ## Gets the name.
            ## @param=path The path to the file
            func probe(path: string) -> string { return $path }
            """);

        var topic = HelpCatalog.ResolveTopic(runtime, "probe", engine.CreateScopedCommandView());
        var argument = Assert.Single(topic!.Arguments!);

        Assert.Equal("path", argument.Name);
        Assert.Equal("The path to the file", argument.Description);
    }
}
