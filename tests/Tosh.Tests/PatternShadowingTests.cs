using System.IO;
using Tosh.Runtime;
using Tosh.Language;
using Tosh.Language.Binding;

namespace Tosh.Tests;

/// <summary>
/// Warning when a pattern binding shadows an enclosing variable — <c>TOAST-0053</c>.
/// </summary>
/// <remarks>
/// <para>
/// Shadowing is legal and sometimes what the author meant, so this is a warning. What it
/// prevents is the silent version: an arm that binds <c>count</c> over an outer <c>$count</c>
/// reads the bound field everywhere in the arm, including where the outer one was meant, and
/// nothing says the name changed meaning.
/// </para>
/// <para>
/// Reaching this needed two fixes to the binder itself. It only walked <c>CommandSyntax</c>
/// stages, so an expression stage — a bare <c>$x + 1</c>, a <c>| where { … }</c>, or any
/// <c>match</c> — was never bound-checked at all. And <c>Strict</c> threw the whole diagnostic
/// batch regardless of severity, so a warning-only run rendered a warning, exited 0, and never
/// executed the program.
/// </para>
/// </remarks>
public sealed class PatternShadowingTests
{
    private const string Union = """
        union R {
            Ok(v: int)
            Err(m: string)
        }
        """;

    private static (ToshEngine Engine, StringWriter Errors) Create()
    {
        var runtime = ToshRuntime.CreateDefault();
        var errors = new StringWriter();
        runtime.Error = errors;
        return (new ToshEngine(runtime.Language), errors);
    }

    [Fact]
    public async Task A_binding_that_shadows_an_outer_variable_is_warned()
    {
        var (engine, errors) = Create();

        var results = await engine.ExecuteToListAsync($$"""
            {{Union}}
            var v = 99
            echo (match (R.Ok(42)) {
                Ok(v) => $v
                default => -1
            })
            """);

        Assert.Contains("pattern_shadows_variable", errors.ToString());
        Assert.Equal("42", results[^1]?.ToString());
    }

    [Fact]
    public async Task A_binding_that_shadows_nothing_is_not_warned()
    {
        var (engine, errors) = Create();

        await engine.ExecuteToListAsync($$"""
            {{Union}}
            echo (match (R.Ok(42)) {
                Ok(v) => $v
                default => -1
            })
            """);

        Assert.DoesNotContain("pattern_shadows_variable", errors.ToString());
    }

    /// <summary>
    /// Bindings are scoped to their arm, so the binder scopes per arm too — otherwise the
    /// second arm to reuse a name would look like it shadowed the first.
    /// </summary>
    [Fact]
    public async Task Two_arms_reusing_a_name_do_not_shadow_each_other()
    {
        var (engine, errors) = Create();

        await engine.ExecuteToListAsync($$"""
            {{Union}}
            echo (match (R.Err("x")) {
                Ok(a) => $a
                Err(a) => $a
                default => "no"
            })
            """);

        Assert.DoesNotContain("pattern_shadows_variable", errors.ToString());
    }

    [Fact]
    public async Task A_rest_binding_that_shadows_is_warned()
    {
        var (engine, errors) = Create();

        await engine.ExecuteToListAsync("""
            var rest = 1
            echo (match ([1, 2, 3]) {
                [f, ...rest] => 0
                default => -1
            })
            """);

        Assert.Contains("pattern_shadows_variable", errors.ToString());
    }

    [Fact]
    public async Task An_as_binding_that_shadows_is_warned()
    {
        var (engine, errors) = Create();

        await engine.ExecuteToListAsync($$"""
            {{Union}}
            var whole = 1
            echo (match (R.Ok(42)) {
                Ok(v) as whole => 0
                default => -1
            })
            """);

        Assert.Contains("pattern_shadows_variable", errors.ToString());
    }

    /// <summary>
    /// A warning must not stop a strict run.
    /// </summary>
    /// <remarks>
    /// <c>Strict</c> threw the whole batch regardless of severity, so this program rendered a
    /// warning, exited 0, and never ran — the one outcome worse than not warning at all.
    /// </remarks>
    [Fact]
    public async Task A_binder_warning_does_not_stop_a_strict_run()
    {
        var (engine, _) = Create();
        using var strict = engine.PushBinderStrictness(BinderStrictness.Strict);

        var results = await engine.ExecuteToListAsync($$"""
            {{Union}}
            var v = 99
            echo (match (R.Ok(42)) {
                Ok(v) => $v
                default => -1
            })
            echo "AFTER"
            """);

        Assert.Equal("AFTER", results[^1]?.ToString());
    }

    [Fact]
    public async Task A_binder_error_still_stops_a_strict_run()
    {
        var (engine, _) = Create();
        using var strict = engine.PushBinderStrictness(BinderStrictness.Strict);

        var error = await Assert.ThrowsAnyAsync<Exception>(async () =>
            await engine.ExecuteToListAsync("""
                var count = 1
                echo $countt
                """));

        Assert.Contains("countt", error.Message);
    }

    /// <summary>
    /// The binder walked only command stages, so nothing inside a <c>match</c> was checked —
    /// not the arms, not even the subject. The runtime reported those instead, from further
    /// away and only if the arm actually ran.
    /// </summary>
    [Fact]
    public async Task The_binder_sees_inside_a_match()
    {
        var (engine, _) = Create();
        using var strict = engine.PushBinderStrictness(BinderStrictness.Strict);

        var inArm = await Assert.ThrowsAnyAsync<Exception>(async () =>
            await engine.ExecuteToListAsync("""
                var count = 1
                echo (match (1) {
                    1 => $countt
                    default => 0
                })
                """));

        Assert.Contains("countt", inArm.Message);

        var (second, _) = Create();
        using var strictAgain = second.PushBinderStrictness(BinderStrictness.Strict);

        var inSubject = await Assert.ThrowsAnyAsync<Exception>(async () =>
            await second.ExecuteToListAsync("""
                var count = 1
                echo (match ($countt) {
                    1 => 5
                    default => 0
                })
                """));

        Assert.Contains("countt", inSubject.Message);
    }

    /// <summary>
    /// And into a bare expression stage, which was skipped for the same reason.
    /// </summary>
    [Fact]
    public async Task The_binder_sees_a_bare_expression_stage()
    {
        var (engine, _) = Create();
        using var strict = engine.PushBinderStrictness(BinderStrictness.Strict);

        var error = await Assert.ThrowsAnyAsync<Exception>(async () =>
            await engine.ExecuteToListAsync("""
                var count = 1
                $countt + 1
                """));

        Assert.Contains("countt", error.Message);
    }
}
