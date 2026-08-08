using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// Introspection sees what the caller sees — <c>TS-P2-54</c> — and "Related" has to be earned —
/// <c>TS-P2-53</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The filed premise was wrong, and measuring it first is what showed that.</b> The item read
/// "a `require`d function is invisible to `help`, although the function is callable". Probing
/// found something wider and simpler: a function declared in a <em>script file</em> is invisible
/// to `help` however it got there — required, sourced, or written in that same file — while the
/// identical source pasted at `-c` works. `require` was incidental. (It was also not quite
/// callable: a plain `func` in a required file is not exported, so `require` brings in nothing;
/// that is `require`'s documented-versus-actual behaviour, filed separately as
/// <c>TS-P2-62</c>.)
/// </para>
/// <para>
/// The cause: a `func` without `global` or `export` registers in the innermost lexical scope, and
/// running a script pushes one. `HelpCatalog` reads <c>runtime.Commands</c> — the global registry
/// — so it never saw it. At `-c` there is no scope to land in, which is exactly why the same
/// script worked when pasted.
/// </para>
/// <para>
/// <c>help</c> was not alone. <c>which fn</c> printed nothing and <c>time "fn"</c> reported that
/// the target was not executable, both for a function the same script had just called. So the
/// fix is a scope-aware view of commands on <c>CommandContext</c> — the twin of the
/// <c>ScopedTypeResolver</c> already carried there for the same reason — rather than a patch
/// inside <c>help</c>.
/// </para>
/// </remarks>
public sealed class ScopedHelpVisibilityTests
{
    private static async Task<string> RunScriptAsync(string source, params (string Name, string Body)[] extraFiles)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"tosh-help-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            foreach (var (name, body) in extraFiles)
            {
                await File.WriteAllTextAsync(Path.Combine(directory, name), body);
            }

            var scriptPath = Path.Combine(directory, "main.tosh");
            await File.WriteAllTextAsync(scriptPath, source);

            var runtime = ToshRuntime.CreateDefault();
            runtime.CurrentDirectory = directory;
            var engine = new ToshEngine(runtime);

            // `ExecuteScriptFileAsync`, not `ExecuteToListAsync`. Running a *file* is what
            // pushes the scope this whole item is about; evaluating the same text as a string
            // is the `-c` path, where the defect never appeared. An earlier draft of these
            // tests used the string form and passed against the unfixed sources.
            var results = new List<object?>();

            await foreach (var value in engine.ExecuteScriptFileAsync(scriptPath))
            {
                results.Add(value);
            }

            return string.Join(",", results.Select(value => value?.ToString() ?? "null"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // ── A script's own functions are introspectable ────────────────────────────

    [Fact]
    public async Task Help_finds_a_function_the_running_script_declared()
    {
        var output = await RunScriptAsync(
            """
            ## Widgetizes a thing.
            func widgetize(thing: string) -> string { return $thing }
            help widgetize | to json
            """);

        Assert.Contains("widgetize", output, StringComparison.Ordinal);
        Assert.Contains("Widgetizes a thing.", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Which_finds_a_function_the_running_script_declared()
    {
        // `which` printed nothing at all — not an error, just no rows, which reads as "there is
        // no such command" for something the previous line had called.
        var output = await RunScriptAsync(
            """
            func widgetize() { return 1 }
            which widgetize | to json
            """);

        Assert.Contains("widgetize", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Time_accepts_a_function_the_running_script_declared()
    {
        var output = await RunScriptAsync(
            """
            func widgetize() { return 7 }
            time "widgetize" | ignore
            "ok"
            """);

        Assert.Contains("ok", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Apropos_finds_a_function_the_running_script_declared()
    {
        var output = await RunScriptAsync(
            """
            ## Widgetizes a thing.
            func widgetize() { return 1 }
            apropos widgetize | to json
            """);

        Assert.Contains("widgetize", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Help_finds_a_sourced_function()
    {
        var output = await RunScriptAsync(
            """
            source "lib.tosh"
            help sourced_fn | to json
            """,
            ("lib.tosh", "## A sourced helper.\nfunc sourced_fn() { return 1 }\n"));

        Assert.Contains("sourced_fn", output, StringComparison.Ordinal);
        Assert.Contains("A sourced helper.", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Help_finds_a_required_export()
    {
        // The item's own case, with the spelling that actually imports something.
        var output = await RunScriptAsync(
            """
            require "./lib.tosh"
            help required_fn | to json
            """,
            ("lib.tosh", "## A required helper.\nexport func required_fn() { return 1 }\n"));

        Assert.Contains("required_fn", output, StringComparison.Ordinal);
        Assert.Contains("A required helper.", output, StringComparison.Ordinal);
    }

    // ── Nothing that already worked changed ────────────────────────────────────

    [Fact]
    public async Task Help_still_finds_a_builtin_from_a_script()
    {
        var output = await RunScriptAsync("help ls | to json");

        Assert.Contains("\"name\"", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ls", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Help_still_reports_a_name_that_exists_nowhere()
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);

        await Assert.ThrowsAnyAsync<Exception>(
            () => engine.ExecuteToListAsync("help no_such_topic_anywhere"));
    }

    [Fact]
    public async Task A_scoped_function_does_not_outlive_its_scope()
    {
        // The view is a snapshot of the scopes the command was called from, so a function
        // declared inside a block is not a permanent addition to the shell's help.
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);

        await engine.ExecuteToListAsync("if (true) { func inner_fn() { return 1 } }");

        Assert.Null(HelpCatalog.ResolveTopic(runtime, "inner_fn"));
    }

    [Fact]
    public void The_global_registry_is_itself_a_view()
    {
        // Which is why a caller with no lexical scope — the `-c` prompt, the TUI, the language
        // server — passes the registry rather than a wrapper around it.
        var runtime = ToshRuntime.CreateDefault();

        Assert.IsAssignableFrom<IScopedCommandView>(runtime.Commands);
        Assert.True(runtime.Commands.TryGet("ls", out _));
    }

    // ── `TS-P2-53`: a relationship has to be earned ────────────────────────────

    [Fact]
    public async Task A_terse_user_function_gets_no_related_list()
    {
        // The reported case: `help add` on a two-line function suggested
        // `xbox · benchmark · compress · cpu-info · dbg`, five commands sharing nothing with it
        // but the category every user function lands in.
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);

        await engine.ExecuteToListAsync(
            "## Adds two numbers.\nfunc add(a: int, b: int) -> int { return ($a + $b) }");

        var topic = HelpCatalog.ResolveTopic(runtime, "add");

        Assert.NotNull(topic);
        Assert.Empty(topic!.Related);
    }

    [Fact]
    public void A_topic_with_genuine_relations_keeps_them()
    {
        // The control that stops this becoming "delete the feature". `each` related to
        // `parallel`, `flat-map`, `map`, `where` and `filter` before the change and still does.
        var runtime = ToshRuntime.CreateDefault();
        var topic = HelpCatalog.ResolveTopic(runtime, "each");

        Assert.NotNull(topic);
        Assert.Contains("map", topic!.Related, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("where", topic.Related, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Declared_relations_are_never_dropped()
    {
        // `func`'s Related list is written down in the catalog rather than computed, and the
        // computed remainder only ever follows it.
        var runtime = ToshRuntime.CreateDefault();
        var topic = HelpCatalog.ResolveTopic(runtime, "func");

        Assert.NotNull(topic);
        Assert.Equal("invoke", topic!.Related[0], ignoreCase: true);
    }

    [Fact]
    public void Most_topics_still_have_relations()
    {
        // The blunt guard against over-tightening: emptying the noisy lists must not empty the
        // corpus. Measured at 318 of 342 when this was written.
        var runtime = ToshRuntime.CreateDefault();
        var topics = HelpCatalog.BuildTopics(runtime);
        var withRelated = topics.Count(topic => topic.Related.Count > 0);

        Assert.True(
            withRelated * 10 >= topics.Count * 8,
            $"only {withRelated} of {topics.Count} topics kept a Related list");
    }

    [Fact]
    public void A_topic_never_relates_to_itself()
    {
        // The computed half also excludes a topic's aliases, and each other's — but this
        // asserts only the self-reference, because a *declared* list may legitimately name one:
        // the `hermit` topic ships with `static` among its relations, and `static` is `hermit`'s
        // own alias. That is catalog data rather than scoring, and pre-dates this change.
        var runtime = ToshRuntime.CreateDefault();

        foreach (var topic in HelpCatalog.BuildTopics(runtime))
        {
            Assert.DoesNotContain(topic.Name, topic.Related, StringComparer.OrdinalIgnoreCase);
        }
    }
}
