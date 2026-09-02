using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// The core <c>Option</c> and <c>Result</c> types — <c>TOAST-0083</c>.
/// </summary>
/// <remarks>
/// <para>
/// Ordinary ToastScript unions loaded as a prelude, not CLR types registered beside
/// <c>Error</c>. As unions they inherit pattern matching, exhaustiveness, the <c>::</c> path
/// operator and serialisation with no special case in the evaluator — which is what the item
/// asks for, and what a CLR implementation would have had to re-earn in
/// <c>TryDescribePatternSubject</c> and the exhaustiveness checker.
/// </para>
/// <para>
/// The prelude is the mechanism the engine already had: <c>BuiltinRunes</c> loads ToastScript at
/// construction, and this loads beside it.
/// </para>
/// <para>
/// The failure variant is <c>Err</c>, not the <c>Error</c> the item's acceptance text names,
/// because <c>Error</c> already names the base class user error types extend — one word should
/// not mean two things in <c>Result::Err(new Error("x"))</c>.
/// </para>
/// </remarks>
public sealed class CorePreludeTests
{
    /// <summary>Strict, the way the CLI runs a script, so a bind-time report throws.</summary>
    private static async Task<string> RunStrictAsync(string source)
    {
        var engine = ShellEngine.CreateFullShell();
        using var strict = engine.PushBinderStrictness(Tosh.Language.Binding.BinderStrictness.Strict);
        var results = await engine.ExecuteToListAsync(source);
        return string.Join(",", results.Select(value => value?.ToString() ?? "null"));
    }

    private static async Task<string> RunAsync(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);
        var results = await engine.ExecuteToListAsync(source);
        return string.Join(",", results.Select(value => value?.ToString() ?? "null"));
    }

    // ── The types exist without an import ──────────────────────────────────────

    [Theory]
    [InlineData("echo (Option::Some(5).Item1)", "5")]
    [InlineData("echo (Option.Some(5).Item1)", "5")]
    [InlineData("func f() -> Result<int, string> { return Result::Ok(3) }\necho ((f()).Item1)", "3")]
    public async Task The_core_types_are_available_with_no_import(string source, string expected)
    {
        Assert.Equal(expected, await RunAsync(source));
    }

    [Fact]
    public async Task A_unit_variant_infers_from_its_target()
    {
        // `TOAST-0096` is what makes this readable; without it every `None` carried its type
        // argument.
        Assert.Equal("empty", await RunAsync(
            """
            var o: Option<int> = Option::None()
            echo (match ($o) {
                None() => "empty"
                default => "other"
            })
            """));
    }

    [Fact]
    public async Task They_pattern_match_like_any_union()
    {
        // Qualified arms included — `TOAST-0095`, which had to be fixed first precisely because
        // `Result::Ok(v)` is the spelling a core type invites.
        Assert.Equal("3", await RunAsync(
            """
            func f() -> Result<int, string> { return Result::Ok(3) }
            echo (match ((f())) {
                Result::Ok(v) => $v
                Result::Err(e) => 0
            })
            """));
    }

    // ── Combinators ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("echo (Option::Some(5).is-some())", "True")]
    [InlineData("echo (Option::Some(5).is-none())", "False")]
    [InlineData("echo (Option::Some(5).unwrap-or(99))", "5")]
    [InlineData("var o: Option<int> = Option::None()\necho ($o.unwrap-or(99))", "99")]
    [InlineData("echo ((Option::Some(5).map(func (x) { return ($x * 2) })).Item1)", "10")]
    [InlineData("echo ((Option::Some(5).and-then(func (x) { return Option::Some($x + 1) })).Item1)", "6")]
    public async Task Option_carries_its_combinators(string source, string expected)
    {
        Assert.Equal(expected, await RunAsync(source));
    }

    [Fact]
    public async Task Mapping_an_absent_option_leaves_it_absent()
    {
        Assert.Equal("True", await RunAsync(
            """
            var o: Option<int> = Option::None()
            echo (($o.map(func (x) { return ($x * 2) })).is-none())
            """));
    }

    [Theory]
    [InlineData("Result::Ok(3)", "(f()).is-ok()", "True")]
    [InlineData("Result::Err(\"bad\")", "(f()).is-err()", "True")]
    [InlineData("Result::Ok(3)", "((f()).map(func (v) { return ($v * 2) })).Item1", "6")]
    [InlineData("Result::Err(\"bad\")", "((f()).map-err(func (e) { return $\"<{$e}>\" })).Item1", "<bad>")]
    [InlineData("Result::Ok(3)", "((f()).ok()).unwrap-or(0)", "3")]
    [InlineData("Result::Err(\"bad\")", "((f()).ok()).is-none()", "True")]
    public async Task Result_carries_its_combinators(string produce, string expression, string expected)
    {
        Assert.Equal(expected, await RunAsync(
            $$"""
            func f() -> Result<int, string> { return {{produce}} }
            echo ({{expression}})
            """));
    }

    [Fact]
    public async Task Mapping_a_failed_result_leaves_the_failure()
    {
        // The asymmetry that makes `Result` worth more than a pair.
        Assert.Equal("True", await RunAsync(
            """
            func f() -> Result<int, string> { return Result::Err("bad") }
            echo (((f()).map(func (v) { return ($v * 2) })).is-err())
            """));
    }

    [Fact]
    public async Task Inspect_returns_the_receiver_unchanged()
    {
        Assert.Equal("True", await RunAsync(
            """
            func f() -> Result<int, string> { return Result::Ok(3) }
            echo (((f()).inspect(func (v) { return null })).is-ok())
            """));
    }

    // ── attempt ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("var r = attempt { 42 }\necho ($r.is-ok())", "True")]
    [InlineData("var r = attempt { 42 }\necho ($r.Item1)", "42")]
    [InlineData("var r = attempt { throw \"boom\" }\necho ($r.is-err())", "True")]
    [InlineData("var r = attempt { throw \"boom\" }\necho ($r.Item1)", "boom")]
    public async Task Attempt_reports_an_outcome_instead_of_raising(string source, string expected)
    {
        Assert.Equal(expected, await RunAsync(source));
    }

    [Fact]
    public async Task Attempt_does_not_capture_a_return()
    {
        // The requirement that makes `attempt` safe to reach for: it converts *failure*, not
        // control flow. `return` travels as a ShellControlFlowException, which `catch` already
        // declines, so it leaves the enclosing function rather than becoming `Ok`.
        Assert.Equal("escaped", await RunAsync(
            """
            func f() {
                var r = attempt { return "escaped" }
                return "captured"
            }
            echo (f())
            """));
    }

    [Fact]
    public async Task Attempt_does_not_capture_a_break()
    {
        Assert.Equal("done", await RunAsync(
            """
            for i in [1, 2, 3] {
                var r = attempt { break }
                echo "body ran"
            }
            echo "done"
            """));
    }

    // ── Exhaustiveness reaches the core types ──────────────────────────────────

    [Fact]
    public async Task An_incomplete_match_over_a_core_type_is_reported()
    {
        // The gap this closed: exhaustiveness was built from the source being bound, so the two
        // types whose whole purpose is exhaustive dispatch were the two without it.
        var error = await Assert.ThrowsAnyAsync<Exception>(async () => await RunStrictAsync(
            """
            func f() -> Result<int, string> { return Result::Ok(3) }
            echo (match ((f())) {
                Ok(v) => $v
            })
            """));

        Assert.Contains("Err", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_complete_match_over_a_core_type_runs()
    {
        Assert.Equal("3", await RunStrictAsync(
            """
            func f() -> Result<int, string> { return Result::Ok(3) }
            echo (match ((f())) {
                Result::Ok(v) => $v
                Result::Err(e) => 0
            })
            """));
    }

    [Fact]
    public async Task A_default_arm_still_satisfies_a_core_type_match()
    {
        Assert.Equal("3", await RunStrictAsync(
            """
            func f() -> Result<int, string> { return Result::Ok(3) }
            echo (match ((f())) {
                Ok(v) => $v
                default => 0
            })
            """));
    }

    [Fact]
    public async Task A_shadowed_union_is_judged_on_its_own_variants()
    {
        // The user's `Result` declares `No`, not `Err`, and that is what must be reported
        // missing — the ambient shape must not leak into a source that replaced it.
        var error = await Assert.ThrowsAnyAsync<Exception>(async () => await RunStrictAsync(
            """
            union Result { Yes(v) No() }
            echo (match ((Result.Yes(1))) {
                Yes(v) => $v
            })
            """));

        Assert.Contains("No", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Err", error.Message, StringComparison.Ordinal);
    }

    // ── null and Option are bridged only by name ───────────────────────────────

    [Theory]
    [InlineData("echo ((option-from null).is-none())", "True")]
    [InlineData("echo ((option-from 5).unwrap-or(0))", "5")]
    [InlineData("echo (Option::Some(5).or-null())", "5")]
    [InlineData("var o: Option<int> = Option::None()\necho (($o.or-null()) is null)", "True")]
    [InlineData("echo ((option-from (Option::Some(7).or-null())).unwrap-or(0))", "7")]
    public async Task A_named_conversion_carries_a_value_across(string source, string expected)
    {
        Assert.Equal(expected, await RunAsync(source));
    }

    [Fact]
    public async Task A_foreign_null_crosses_by_the_same_name()
    {
        // The CLR has no Option to offer, so its nulls arrive as nulls and are named across
        // like any other. There is no special foreign-boundary rule.
        Assert.Equal("True", await RunAsync(
            """
            echo ((option-from (System::Environment::GetEnvironmentVariable("NOPE_XYZ"))).is-none())
            """));
    }

    [Theory]
    [InlineData("var o: Option<int> = null")]
    [InlineData("var o: Option<string> = System::Environment::GetEnvironmentVariable(\"NOPE_XYZ\")")]
    [InlineData("var o = Option::Some(3)\nvar x: int? = $o")]
    public async Task Neither_direction_converts_on_its_own(string source)
    {
        // `T?` says a slot may hold nothing; `Option<T>` says absence is part of the domain.
        // Decided 2026-08-29: neither becomes the other implicitly, so optionality cannot
        // silently appear or disappear.
        await Assert.ThrowsAnyAsync<Exception>(async () => await RunAsync(source));
    }

    [Fact]
    public async Task A_nullable_slot_still_takes_null()
    {
        // The control: the RFC's "`T?` admits `null`; an ordinary `T` does not" is unchanged.
        Assert.Equal("True", await RunAsync(
            """
            var x: int? = null
            echo ($x is null)
            """));
    }

    // ── A checker's diagnostics ride in a Result ───────────────────────────────

    private const string Checker =
        """
        record CpDiag(Code: string, Message: string)

        func cp-check(name) -> Result<string, list<CpDiag>> {
            var errors = new list<CpDiag>()

            if ($name == "") {
                var _ = $errors.Add(new CpDiag("empty", "identifier is empty"))
            }

            if ($name.Length > 8) {
                var _ = $errors.Add(new CpDiag("too-long", "identifier exceeds 8 characters"))
            }

            if ($name contains " ") {
                var _ = $errors.Add(new CpDiag("has-space", "identifier contains a space"))
            }

            if ($errors.Count > 0) {
                return Result::Err($errors)
            }

            return Result::Ok($name)
        }

        """;

    [Theory]
    [InlineData("\"ok\"", "ok: ok")]
    [InlineData("\"\"", "errs: 1 first=empty")]
    [InlineData("\"waytoolongidentifier\"", "errs: 1 first=too-long")]
    public async Task Expected_failures_accumulate_in_a_result(string argument, string expected)
    {
        // The shape the self-hosting compiler needs: every problem with the input collected and
        // returned, rather than the first one raised.
        Assert.Equal(expected, await RunAsync(
            Checker +
            $$"""
            var r = cp-check {{argument}}
            echo (match ($r) {
                Ok(v) => $"ok: {$v}"
                Err(e) => $"errs: {$e.Count} first={$e[0].Code}"
            })
            """));
    }

    [Fact]
    public async Task All_problems_are_collected_not_just_the_first()
    {
        // Both rules fire for this one, and a checker that stopped at the first problem would
        // report half of what is wrong with it.
        Assert.Equal("errs: 2", await RunAsync(
            Checker +
            """
            var r = cp-check "way too long"
            echo (match ($r) {
                Ok(v) => "ok"
                Err(e) => $"errs: {$e.Count}"
            })
            """));
    }

    [Fact]
    public async Task An_invariant_failure_still_throws()
    {
        // The other half of the contract: `Result` carries *expected* failure. A broken
        // invariant is not one, and must not be quietly turned into an `Err` that a caller
        // might handle as ordinary input trouble.
        await Assert.ThrowsAnyAsync<Exception>(async () => await RunAsync(
            Checker +
            """
            func cp-broken() -> Result<string, list<CpDiag>> {
                throw "invariant violated"
            }
            cp-broken()
            """));
    }

    // ── Shadowing ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_user_declaration_wins()
    {
        // The rule the parser already documents: a bare name is where a declaration should win.
        // The 12 sites in this repo that already declare a `union Result` depend on it.
        Assert.Equal("mine", await RunAsync(
            """
            union Result { Yes(v) No() }
            echo (match ((Result.Yes(1))) {
                Ok(v) => "core"
                Yes(v) => "mine"
                default => "?"
            })
            """));
    }

    [Fact]
    public async Task Shadowing_is_reported_but_does_not_fail()
    {
        // A warning, never an error: making it an error would break every existing declaration
        // of these very common names.
        Assert.Equal("1", await RunAsync(
            """
            union Option { Yes(v) No() }
            echo (Option.Yes(1).v)
            """));
    }
}
