using System.IO;
using Tosh.Language;
using Tosh.Language.Binding;
using Tosh.Language.Parsing;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// Tests for the binder pass that runs between parse and evaluate. The
/// binder resolves command names against the runtime registry plus
/// same-source function declarations and surfaces a diagnostic when an
/// unresolved name has a close Levenshtein match to a registered command.
/// </summary>
public sealed class BinderTests : IClassFixture<ToshRuntimeFixture>
{
    private readonly ToshRuntime _runtime;

    public BinderTests(ToshRuntimeFixture fixture)
    {
        _runtime = fixture.Runtime;
    }

    private ParseResult ParseSource(string source)
    {
        var engine = new ToshEngine(_runtime);
        return engine.Parse(source, "<binder-test>");
    }

    [Fact]
    public void Bind_returns_no_diagnostics_for_registered_builtins()
    {
        var parse = ParseSource("ls | where _ != null | first");
        var diagnostics = Binder.Bind(parse, _runtime.Commands, isExecutableOnPath: _ => false);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void Bind_flags_likely_typo_with_did_you_mean_suggestion()
    {
        // 'flatmap' is a single-edit miss for 'flat-map' ... actually 2 edits
        // ('-' insertion + length delta). Use 'flatmpa' which is distance 2
        // from 'flat-map' is too far. The known builtin is 'flat-map';
        // 'flatamp' is distance 2 → matches threshold for length > 4.
        var parse = ParseSource("[1,2,3] | flatmap { _ }");
        var diagnostics = Binder.Bind(parse, _runtime.Commands, isExecutableOnPath: _ => false);

        Assert.NotEmpty(diagnostics);
        var diag = diagnostics[0];
        Assert.Equal("tosh.bind.unknown_command", diag.Code);
        Assert.Contains("flatmap", diag.Title, StringComparison.Ordinal);
        Assert.Contains("did you mean", diag.Label!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("flat-map", diag.Label!, StringComparison.Ordinal);
    }

    [Fact]
    public void Bind_typo_for_short_command_name_uses_distance_one_threshold()
    {
        // 'lz' (length 2) → threshold 1 → matches 'ls' (distance 1).
        var parse = ParseSource("lz");
        var diagnostics = Binder.Bind(parse, _runtime.Commands, isExecutableOnPath: _ => false);

        Assert.NotEmpty(diagnostics);
        Assert.Contains("ls", diagnostics[0].Label!, StringComparison.Ordinal);
    }

    [Fact]
    public void Bind_silent_when_unresolved_name_has_no_close_match()
    {
        // Defer to runtime — could be an external on PATH.
        var parse = ParseSource("totally-unique-name-with-no-similar-builtin");
        var diagnostics = Binder.Bind(parse, _runtime.Commands, isExecutableOnPath: _ => false);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void Bind_skips_explicit_paths()
    {
        var parse = ParseSource("./flatmap arg");
        var diagnostics = Binder.Bind(parse, _runtime.Commands, isExecutableOnPath: _ => false);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void Bind_skips_dollar_prefixed_variable_invocations()
    {
        var parse = ParseSource("$callable arg1 arg2");
        var diagnostics = Binder.Bind(parse, _runtime.Commands, isExecutableOnPath: _ => false);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void Bind_recognizes_same_source_function_declaration()
    {
        var parse = ParseSource("""
            func myproc(x) { echo $x }
            myproc 42
            """);
        var diagnostics = Binder.Bind(parse, _runtime.Commands, isExecutableOnPath: _ => false);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void Bind_recognizes_forward_referenced_function()
    {
        var parse = ParseSource("""
            myproc 42
            func myproc(x) { echo $x }
            """);
        var diagnostics = Binder.Bind(parse, _runtime.Commands, isExecutableOnPath: _ => false);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void Bind_recurses_into_function_body()
    {
        // 'eccho' is distance 1 from 'echo' (length > 4 → threshold 2).
        var parse = ParseSource("""
            func greet(n) { eccho $n }
            """);
        var diagnostics = Binder.Bind(parse, _runtime.Commands, isExecutableOnPath: _ => false);
        Assert.NotEmpty(diagnostics);
        Assert.Contains("echo", diagnostics[0].Label!, StringComparison.Ordinal);
    }

    [Fact]
    public void Bind_recurses_into_block_argument_of_pipeline_command()
    {
        var parse = ParseSource("[1,2,3] | each { eccho _ }");
        var diagnostics = Binder.Bind(parse, _runtime.Commands, isExecutableOnPath: _ => false);
        Assert.NotEmpty(diagnostics);
        Assert.Contains("echo", diagnostics[0].Label!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Engine_warns_on_typo_under_default_strictness_but_does_not_throw()
    {
        // Default REPL-style behaviour: the binder warning hits stderr but
        // the runtime still attempts resolution (and ultimately fails with
        // its own diagnostic since the typo is also not on PATH). We check
        // here that the binder did not promote to an exception under Warn.
        var stderr = new StringWriter();
        var runtime = ToshRuntime.CreateDefault(TextWriter.Null, stderr);
        var engine = new ToshEngine(runtime);
        Assert.Equal(BinderStrictness.Warn, engine.BinderStrictness);

        await Assert.ThrowsAsync<ToshDiagnosticException>(async () =>
            await engine.ExecuteToListAsync("flatmap"));

        Assert.Contains("flat-map", stderr.ToString(), StringComparison.Ordinal);
        Assert.Contains("flatmap", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Engine_throws_binder_diagnostic_under_strict_strictness()
    {
        var runtime = ToshRuntime.CreateDefault(TextWriter.Null, TextWriter.Null);
        var engine = new ToshEngine(runtime);
        engine.BinderStrictness = BinderStrictness.Strict;

        var ex = await Assert.ThrowsAsync<ToshDiagnosticException>(async () =>
            await engine.ExecuteToListAsync("flatmap"));

        Assert.Equal("tosh.bind.unknown_command", ex.Diagnostics[0].Code);
    }

    [Fact]
    public async Task Engine_emits_no_diagnostic_under_lenient_strictness()
    {
        var stderr = new StringWriter();
        var runtime = ToshRuntime.CreateDefault(TextWriter.Null, stderr);
        var engine = new ToshEngine(runtime);
        engine.BinderStrictness = BinderStrictness.Lenient;

        // Still throws at runtime because flatmap is not a registered command and
        // not on PATH; but the binder produced no warning under Lenient.
        await Assert.ThrowsAsync<ToshDiagnosticException>(async () =>
            await engine.ExecuteToListAsync("flatmap"));

        Assert.DoesNotContain("did you mean", stderr.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PushBinderStrictness_restores_previous_value_on_dispose()
    {
        var engine = new ToshEngine(_runtime);
        var original = engine.BinderStrictness;

        using (engine.PushBinderStrictness(BinderStrictness.Strict))
        {
            Assert.Equal(BinderStrictness.Strict, engine.BinderStrictness);
        }

        Assert.Equal(original, engine.BinderStrictness);
    }

    [Fact]
    public async Task TOSH_DISABLE_BINDER_env_var_short_circuits_binder()
    {
        var stderr = new StringWriter();
        var runtime = ToshRuntime.CreateDefault(TextWriter.Null, stderr);
        var engine = new ToshEngine(runtime);
        engine.BinderStrictness = BinderStrictness.Strict;

        var previous = Environment.GetEnvironmentVariable("TOSH_DISABLE_BINDER");
        Environment.SetEnvironmentVariable("TOSH_DISABLE_BINDER", "1");
        try
        {
            // Under Strict the binder would normally throw on 'flatmap'. With
            // the bailout active, evaluation proceeds and the runtime's own
            // command-not-found diagnostic surfaces instead.
            var ex = await Assert.ThrowsAsync<ToshDiagnosticException>(async () =>
                await engine.ExecuteToListAsync("flatmap"));

            Assert.NotEqual("tosh.bind.unknown_command", ex.Diagnostics[0].Code);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TOSH_DISABLE_BINDER", previous);
        }
    }

    [Fact]
    public void Bind_does_not_flag_shell_only_command_in_interactive_context()
    {
        // 'back' is a [ShellOnly] command (directory-stack pop).
        var parse = ParseSource("back");
        var diagnostics = Binder.Bind(parse, _runtime.Commands, isInteractive: true);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void Bind_flags_shell_only_command_in_non_interactive_context()
    {
        var parse = ParseSource("back");
        var diagnostics = Binder.Bind(parse, _runtime.Commands, isInteractive: false);
        Assert.NotEmpty(diagnostics);
        Assert.Equal("tosh.shell_only", diagnostics[0].Code);
        Assert.Contains("back", diagnostics[0].Label!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Engine_throws_at_bind_time_for_shell_only_command_in_script_mode()
    {
        var runtime = ToshRuntime.CreateDefault(TextWriter.Null, TextWriter.Null);
        var engine = new ToshEngine(runtime);
        // Default IsInteractiveSession=false; default Strictness=Warn.
        // Binder emits tosh.shell_only as a Warn under Warn, but under Strict
        // (which scripts use) it throws before any evaluation runs.
        engine.BinderStrictness = BinderStrictness.Strict;

        var ex = await Assert.ThrowsAsync<ToshDiagnosticException>(async () =>
            await engine.ExecuteToListAsync("if (false) { back } else { echo hi }"));

        // The diagnostic is bind-time even though the command is unreachable.
        Assert.Equal("tosh.shell_only", ex.Diagnostics[0].Code);
    }

    [Fact]
    public async Task Engine_does_not_flag_shell_only_command_in_interactive_session()
    {
        var runtime = ToshRuntime.CreateDefault(TextWriter.Null, TextWriter.Null);
        var engine = new ToshEngine(runtime) { IsInteractiveSession = true };
        engine.BinderStrictness = BinderStrictness.Strict;

        // Even under Strict, an interactive session must let shell-only
        // commands through the binder. (The command itself may still fail
        // for runtime reasons in a unit-test context, but not at bind time.)
        try
        {
            await engine.ExecuteToListAsync("dirs");
        }
        catch (ToshDiagnosticException ex)
        {
            Assert.NotEqual("tosh.shell_only", ex.Diagnostics[0].Code);
        }
    }

    // ──────────────────────────────────────────────────────────────────
    // Phase 2: variable-name binder
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Variable_binder_does_not_flag_in_scope_reference()
    {
        var parse = ParseSource("""
            var name = "alice"
            echo $name
            """);
        var diags = Binder.Bind(parse, _runtime.Commands, isExecutableOnPath: _ => false);
        Assert.DoesNotContain(diags, d => d.Code == "tosh.bind.unknown_variable");
    }

    [Fact]
    public void Variable_binder_flags_typo_with_did_you_mean()
    {
        // 'nme' is distance 1 from 'name' (short-name threshold 1).
        var parse = ParseSource("""
            var name = "alice"
            echo $nme
            """);
        var diags = Binder.Bind(parse, _runtime.Commands, isExecutableOnPath: _ => false);
        var diag = Assert.Single(diags, d => d.Code == "tosh.bind.unknown_variable");
        Assert.Contains("$name", diag.Label!, StringComparison.Ordinal);
    }

    [Fact]
    public void Variable_binder_skips_references_with_no_near_match()
    {
        // No declared name remotely close to 'completely_unknown' — likely
        // an externally-set variable; defer to runtime.
        var parse = ParseSource("echo $completely_unknown");
        var diags = Binder.Bind(parse, _runtime.Commands, isExecutableOnPath: _ => false);
        Assert.DoesNotContain(diags, d => d.Code == "tosh.bind.unknown_variable");
    }

    [Fact]
    public void Variable_binder_allows_special_namespaces()
    {
        var parse = ParseSource("""
            echo $env.HOME
            echo $tosh.Config.Shell.Dirs
            echo $args
            """);
        var diags = Binder.Bind(parse, _runtime.Commands, isExecutableOnPath: _ => false);
        Assert.DoesNotContain(diags, d => d.Code == "tosh.bind.unknown_variable");
    }

    [Fact]
    public void Variable_binder_allows_function_parameters()
    {
        var parse = ParseSource("""
            func greet(name) { echo $name }
            """);
        var diags = Binder.Bind(parse, _runtime.Commands, isExecutableOnPath: _ => false);
        Assert.DoesNotContain(diags, d => d.Code == "tosh.bind.unknown_variable");
    }

    [Fact]
    public void Variable_binder_flags_param_typo_inside_function_body()
    {
        var parse = ParseSource("""
            func greet(name) { echo $nme }
            """);
        var diags = Binder.Bind(parse, _runtime.Commands, isExecutableOnPath: _ => false);
        Assert.Contains(diags, d => d.Code == "tosh.bind.unknown_variable");
    }

    [Fact]
    public void Variable_binder_allows_for_loop_variable()
    {
        var parse = ParseSource("""
            for $item in [1, 2, 3] { echo $item }
            """);
        var diags = Binder.Bind(parse, _runtime.Commands, isExecutableOnPath: _ => false);
        Assert.DoesNotContain(diags, d => d.Code == "tosh.bind.unknown_variable");
    }

    [Fact]
    public void Variable_binder_allows_catch_variable()
    {
        var parse = ParseSource("""
            try { echo hi } catch $err { echo $err }
            """);
        var diags = Binder.Bind(parse, _runtime.Commands, isExecutableOnPath: _ => false);
        Assert.DoesNotContain(diags, d => d.Code == "tosh.bind.unknown_variable");
    }

    [Fact]
    public void Variable_binder_does_not_flag_member_tail()
    {
        // The binder cannot know record shapes; only the root is checked.
        var parse = ParseSource("""
            var person = {| name: "alice", age: 30 |}
            echo $person.notarealfield.namee
            """);
        var diags = Binder.Bind(parse, _runtime.Commands, isExecutableOnPath: _ => false);
        Assert.DoesNotContain(diags, d => d.Code == "tosh.bind.unknown_variable");
    }

    [Fact]
    public void Variable_binder_does_not_flag_pipeline_underscore()
    {
        var parse = ParseSource("""
            var nums = [1, 2, 3]
            echo $nums | where $_ > 1
            """);
        var diags = Binder.Bind(parse, _runtime.Commands, isExecutableOnPath: _ => false);
        Assert.DoesNotContain(diags, d => d.Code == "tosh.bind.unknown_variable");
    }

    [Fact]
    public void Variable_binder_flags_typo_inside_interpolated_string()
    {
        // Tosh interpolation form: $"...{expr}..." where expr is parsed
        // as a tosh source fragment, so variable references take the
        // usual $name form.
        var parse = ParseSource("""
            var name = "alice"
            echo $"hello, {$nme}"
            """);
        var diags = Binder.Bind(parse, _runtime.Commands, isExecutableOnPath: _ => false);
        Assert.Contains(diags, d => d.Code == "tosh.bind.unknown_variable");
    }
    [Fact]
    public void Variable_binder_interpolated_string_diagnostic_points_at_hole()
    {
        // The precise span should cover the '$nme' inside the interpolation
        // hole, not the entire string literal.
        var source = """
            var name = "alice"
            echo $"hello, {$nme}"
            """;
        var parse = ParseSource(source);
        var diags = Binder.Bind(parse, _runtime.Commands, isExecutableOnPath: _ => false);
        var diag = Assert.Single(diags, d => d.Code == "tosh.bind.unknown_variable");

        Assert.NotNull(diag.Span);
        var span = diag.Span!.Value;
        var slice = source.Substring(span.Start, span.Length);
        Assert.Equal("$nme", slice);
    }
    [Fact]
    public void Variable_binder_allows_destructured_names()
    {
        var parse = ParseSource("""
            var [a, b] = [1, 2]
            echo $a $b
            """);
        var diags = Binder.Bind(parse, _runtime.Commands, isExecutableOnPath: _ => false);
        Assert.DoesNotContain(diags, d => d.Code == "tosh.bind.unknown_variable");
    }

    [Fact]
    public void Variable_binder_allows_lambda_parameters()
    {
        var parse = ParseSource("""
            [1, 2, 3] | each |x| { echo $x }
            """);
        var diags = Binder.Bind(parse, _runtime.Commands, isExecutableOnPath: _ => false);
        Assert.DoesNotContain(diags, d => d.Code == "tosh.bind.unknown_variable");
    }

}
