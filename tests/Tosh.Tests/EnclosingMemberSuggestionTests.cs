using Tosh.Language;
using Tosh.Language.Binding;
using Tosh.Language.Parsing;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// A bare name that names a member of the enclosing class is answered with the qualified form
/// rather than with shell commands — <c>TS-P2-41</c>.
/// </summary>
/// <remarks>
/// <para>
/// The resolution rule is not changed and is not at fault: members are reached through
/// <c>ClassName.</c> or <c>$this.</c>, and bare <c>f()</c> fails uniformly from static and
/// instance positions alike. What was wrong is the account given of the failure —
/// <c>static prop Y =&gt; f()</c> beside <c>static func f()</c> answered "did you mean 'df',
/// 'fg', or 'if'?", three unrelated shell commands, when a member of the enclosing class
/// differed by nothing at all.
/// </para>
/// <para>
/// The shell has two suggestion machines and this programme has already watched a guard fixed in
/// one come back through the other (<c>TS-P1-24</c>), so the binder and the engine share one rule
/// here and the tests pin both. Every suggested spelling below is also *executed*, because a
/// suggestion that does not work is worse than none: a first draft offered
/// <c>$this.name</c> for a primary-constructor parameter, which fails — such a parameter is not a
/// member at all, and is in scope inside a property initializer — where it already resolves, so
/// the binder's job there is to stay quiet, which it was not doing.
/// </para>
/// </remarks>
public sealed class EnclosingMemberSuggestionTests : IClassFixture<ToshRuntimeFixture>
{
    private readonly ToshRuntime _runtime;

    public EnclosingMemberSuggestionTests(ToshRuntimeFixture fixture) => _runtime = fixture.Runtime;

    private IReadOnlyList<ToshDiagnostic> Bind(string source)
    {
        var engine = new ToshEngine(_runtime);
        var parse = engine.Parse(source, "<enclosing-member-test>");
        return Binder.Bind(parse, _runtime.Commands, isExecutableOnPath: _ => false);
    }

    private ToshDiagnostic BindOne(string source) => Assert.Single(Bind(source));

    private async Task<object?> EvalAsync(string source)
    {
        var engine = new ToshEngine(new ToshRuntime());
        return (await engine.ExecuteToListAsync(source)).LastOrDefault();
    }

    // ── the suggestion names the member, not a shell command ───────────────────

    [Theory]
    [InlineData("class K { static func f() { return 7 }\nstatic prop Y => f() }", "K.f()")]
    [InlineData("class K { func zog() { return 7 }\nprop Y => zog() }", "$this.zog()")]
    [InlineData("class K { prop Alpha: int = 5\nprop Y => Alpha }", "$this.Alpha")]
    [InlineData("class K { static prop S: int = 5\nprop B: int = S }", "K.S")]
    [InlineData("class K { static func f() { return 7 }\nfunc g() { return f() } }", "K.f()")]
    public void A_sibling_member_is_suggested_in_its_qualified_form(string source, string expected)
    {
        var diagnostic = BindOne(source);

        Assert.Equal("tosh.bind.unknown_command", diagnostic.Code);
        Assert.Equal($"did you mean '{expected}'?", diagnostic.Label);
        Assert.Contains("declared by 'K'", diagnostic.Title, StringComparison.Ordinal);
        Assert.Contains("resolved as a command, not as a member", diagnostic.Help, StringComparison.Ordinal);
    }

    [Fact]
    public void The_board_s_reported_case_no_longer_names_three_shell_commands()
    {
        // Reported as: "did you mean 'df', 'fg', or 'if'?"
        var diagnostic = BindOne("class K { static func f() { return 7 }\nstatic prop Y => f() }");

        Assert.DoesNotContain("df", diagnostic.Label, StringComparison.Ordinal);
        Assert.DoesNotContain("fg", diagnostic.Label, StringComparison.Ordinal);
        Assert.Equal("did you mean 'K.f()'?", diagnostic.Label);
    }

    // ── the suggested spellings have to work ───────────────────────────────────

    [Theory]
    [InlineData("class K { static func f() { return 7 }\nstatic prop Y => K.f() }\nK.Y", 7)]
    [InlineData("class K { func zog() { return 7 }\nprop Y => $this.zog() }\nvar k = new K()\n$k.Y", 7)]
    [InlineData("class K { prop Alpha: int = 5\nprop Y => $this.Alpha }\nvar k = new K()\n$k.Y", 5)]
    [InlineData("class K { static prop S: int = 5\nprop B: int = K.S }\nvar k = new K()\n$k.B", 5)]
    public async Task Every_suggested_spelling_resolves(string source, int expected)
    {
        Assert.Equal(expected, Convert.ToInt32(await EvalAsync(source)));
    }

    // ── a primary-constructor parameter, which is not a member ─────────────────

    [Theory]
    [InlineData("class P(x: int) { prop X = x }\nvar p = new P(5)\n$p.X", 5)]
    [InlineData("class P(x: int) { prop X = $x }\nvar p = new P(5)\n$p.X", 5)]
    [InlineData("class K(name: string) { prop Y = name }\nvar k = new K(\"7\")\n$k.Y", 7)]
    public async Task A_constructor_parameter_resolves_inside_an_initializer(string source, int expected)
    {
        // Both spellings work there, and nowhere else in the body. The third case was *rejected*
        // before this change — `name` resembles `uname`, so the typo machine refused a program
        // that ran correctly under TOSH_DISABLE_BINDER=1. The first is the shape the ToSh SDK's
        // own fixture uses, which is what caught a draft of this fix that had the rule backwards.
        Assert.Equal(expected, Convert.ToInt32(await EvalAsync(source)));
    }

    [Theory]
    [InlineData("class P(x: int) { prop X = x }")]
    [InlineData("class K(name: string) { prop Y = name }")]
    public void A_constructor_parameter_is_not_flagged_inside_an_initializer(string source)
    {
        Assert.Empty(Bind(source));
    }

    [Fact]
    public void A_constructor_parameter_outside_an_initializer_is_told_where_it_reaches()
    {
        // Genuinely unresolvable: neither `x` nor `$x` works in a method body, and the parameter
        // is not a member, so there is no qualified form to offer. Naming its scope beats naming
        // a command that merely looks like it.
        var diagnostic = BindOne("class P(x: int) { func g() { return x } }");

        Assert.Contains("constructor parameter of 'P'", diagnostic.Title, StringComparison.Ordinal);
        Assert.Contains("only in a property initializer", diagnostic.Help, StringComparison.Ordinal);
    }

    // ── what must not change ───────────────────────────────────────────────────

    [Fact]
    public void A_real_executable_of_the_same_name_still_wins()
    {
        // A class that declares `prop git` must not stop `git status` in one of its methods.
        var engine = new ToshEngine(_runtime);
        var parse = engine.Parse("class K { prop git: int = 1\nfunc g() { git --version } }", "<probe>");

        var diagnostics = Binder.Bind(parse, _runtime.Commands, isExecutableOnPath: name => name == "git");

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void A_name_matching_nothing_in_the_class_keeps_the_old_answer()
    {
        var diagnostic = BindOne("class K { func g() { f } }");

        Assert.DoesNotContain("declared by", diagnostic.Title, StringComparison.Ordinal);
        Assert.Contains("did you mean", diagnostic.Label, StringComparison.Ordinal);
    }

    [Fact]
    public void The_enclosing_class_does_not_leak_past_its_own_body()
    {
        // `f` after the class is an ordinary unresolved name again, not a member of `K`.
        var diagnostic = BindOne("class K { static func f() { return 7 } }\nf");

        Assert.DoesNotContain("declared by", diagnostic.Title, StringComparison.Ordinal);
    }

    // ── a struct body is walked exactly as a class body is (`TS-P2-80`) ────────

    [Theory]
    // The same typo in each container. `struct` reported nothing at all: `Binder.VisitStatement`
    // had a `ClassDefinitionStatementSyntax` case and no struct one, and `CollectLocalFunctions`
    // had the same gap — so the body was never visited and the suggestion above could not fire
    // inside one however well it worked elsewhere.
    [InlineData("func h() { f }")]
    [InlineData("class K { func g() { f } }")]
    [InlineData("struct S { func g() { f } }")]
    [InlineData("module M { func g() { f } }")]
    public void A_typo_is_caught_in_every_container(string source)
    {
        Assert.Contains(Bind(source), d => d.Code == "tosh.bind.unknown_command");
    }

    [Theory]
    [InlineData("struct S { func zog() { return 7 }\nfunc g() { return zog() } }", "$this.zog()")]
    [InlineData("struct S { prop A: int = 0\nprop B: int = A }", "$this.A")]
    [InlineData("struct S { shared func f() { return 7 }\nfunc g() { return f() } }", "S.f()")]
    public void A_struct_member_is_suggested_in_its_qualified_form(string source, string expected)
    {
        var diagnostic = BindOne(source);

        Assert.Equal($"did you mean '{expected}'?", diagnostic.Label);
        Assert.Contains("declared by 'S'", diagnostic.Title, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_function_declared_inside_a_struct_method_still_resolves()
    {
        // Collecting local functions from struct bodies is the other half of the walk; without
        // it, visiting the body would newly flag a call that resolves perfectly well.
        var engine = new ToshEngine(_runtime);
        var results = await engine.ExecuteToListAsync(
            "struct S { func g() { func inner() { return 3 }\nreturn inner() } }\nvar s = new S()\n$s.g()");

        Assert.Equal(3, Convert.ToInt32(results.LastOrDefault()));
    }

    [Fact]
    public void A_same_source_top_level_function_still_shadows_the_member()
    {
        // The bare name resolves to the top-level function at runtime, so the binder must stay
        // quiet rather than suggest a member the call is not reaching for.
        Assert.Empty(Bind("func f() { return 1 }\nclass K { static func f() { return 7 }\nstatic prop Y => f() }"));
    }

    // ── the engine's half, reached when the binder cannot see the source ───────

    [Fact]
    public async Task The_runtime_gives_the_same_answer_when_the_binder_is_suppressed()
    {
        // A source that `require`s cannot be bound — its imports are invisible — so the binder
        // defers and the engine's resolution raises instead. Both must say the same thing, which
        // is why the wording lives in one place.
        var directory = Path.Combine(Path.GetTempPath(), $"tosh-p2-41-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var library = Path.Combine(directory, "lib.tosh");
            await File.WriteAllTextAsync(library, "export var LIBV = 1\n");

            var engine = new ToshEngine(new ToshRuntime());
            var exception = await Assert.ThrowsAsync<ToshDiagnosticException>(async () =>
                await engine.ExecuteToListAsync(
                    $"require \"{library.Replace("\\", "/")}\"\n" +
                    "class K { static func zog() { return 7 }\nstatic prop Y => zog() }\nK.Y"));

            var diagnostic = Assert.Single(exception.Diagnostics, d => d.Code == "tosh.runtime.unknown_command");
            Assert.Equal("did you mean 'K.zog()'?", diagnostic.Label);
            Assert.Contains("declared by 'K'", diagnostic.Title, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
