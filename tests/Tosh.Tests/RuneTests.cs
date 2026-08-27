using System.Linq;
using Tosh.Runtime;
using Tosh.Language;

namespace Tosh.Tests;

public sealed class RuneTests
{
    // --- Basic rune definition and invocation ---

    [Fact]
    public async Task Simple_rune_executes_body()
    {
        var engine = ShellEngine.CreateFullShell();
        var results = await engine.ExecuteToListAsync("""
            rune greet(name) {
                echo $"Hello, {$name}!"
            }
            greet "World"
            """);
        Assert.Equal(new object[] { "Hello, World!" }, results);
    }

    [Fact]
    public async Task Rune_with_multiple_parameters()
    {
        var engine = ShellEngine.CreateFullShell();
        var results = await engine.ExecuteToListAsync("""
            rune add(a, b) {
                echo ($a + $b)
            }
            add 3 4
            """);
        Assert.Equal(new object[] { 7 }, results);
    }

    [Fact]
    public async Task Rune_with_block_argument()
    {
        var engine = ShellEngine.CreateFullShell();
        var results = await engine.ExecuteToListAsync("""
            rune do-twice(body) {
                $body
                $body
            }
            do-twice { echo "hi" }
            """);
        Assert.Equal(new object[] { "hi", "hi" }, results);
    }

    // --- Hygiene (sealed vs leaky) ---

    [Fact]
    public async Task Sealed_rune_does_not_leak_internal_variables()
    {
        var engine = ShellEngine.CreateFullShell();
        await Assert.ThrowsAsync<ToshDiagnosticException>(async () =>
        {
            await engine.ExecuteToListAsync("""
                rune set-internal() {
                    var __secret = 42
                }
                set-internal
                echo $__secret
                """);
        });
    }

    [Fact]
    public async Task Leaky_rune_exposes_internal_variables()
    {
        var engine = ShellEngine.CreateFullShell();
        var results = await engine.ExecuteToListAsync("""
            leaky rune set-var() {
                var leaked = 42
            }
            set-var
            echo $leaked
            """);
        Assert.Equal(new object[] { 42 }, results);
    }

    // --- Quote ---

    [Fact]
    public async Task Quote_captures_source_text()
    {
        var engine = ShellEngine.CreateFullShell();
        var results = await engine.ExecuteToListAsync("""
            rune show-source(expr) {
                var src = (quote { $expr })
                echo $src
            }
            show-source (1 + 2)
            """);
        Assert.Single(results);
        Assert.Contains("1 + 2", results[0]?.ToString());
    }

    // --- Pipeline input passthrough ---

    [Fact]
    public async Task Rune_receives_pipeline_input()
    {
        var engine = ShellEngine.CreateFullShell();
        var results = await engine.ExecuteToListAsync("""
            rune pass-through(x) {
                echo $"got {$x}"
            }
            pass-through "data"
            """);
        Assert.Equal(new object[] { "got data" }, results);
    }

    // --- Built-in runes ---

    [Fact]
    public async Task Builtin_unless_skips_when_true()
    {
        var engine = ShellEngine.CreateFullShell();
        var results = await engine.ExecuteToListAsync("""
            unless true { echo "should not appear" }
            echo "done"
            """);
        Assert.Equal(new object[] { "done" }, results);
    }

    [Fact]
    public async Task Builtin_unless_executes_when_false()
    {
        var engine = ShellEngine.CreateFullShell();
        var results = await engine.ExecuteToListAsync("""
            unless false { echo "appeared" }
            """);
        Assert.Equal(new object[] { "appeared" }, results);
    }

    // --- Error cases ---

    [Fact]
    public async Task Rune_with_wrong_arg_count_throws()
    {
        var engine = ShellEngine.CreateFullShell();
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await engine.ExecuteToListAsync("""
                rune needs-two(a, b) { echo "ok" }
                needs-two "only-one"
                """);
        });
    }

    [Fact]
    public async Task Rune_with_duplicate_params_throws()
    {
        var engine = ShellEngine.CreateFullShell();
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await engine.ExecuteToListAsync("""
                rune bad(x, x) { echo "bad" }
                """);
        });
    }

    // --- Nested rune invocation ---

    [Fact]
    public async Task Nested_rune_invocation()
    {
        var engine = ShellEngine.CreateFullShell();
        var results = await engine.ExecuteToListAsync("""
            rune wrap(x) {
                echo $"[{$x}]"
            }
            rune double-wrap(x) {
                wrap $"({$x})"
            }
            double-wrap "hi"
            """);
        Assert.Equal(new object[] { "[(hi)]" }, results);
    }

    // --- Sealed scope: the argument is evaluated where it was written (`TOAST-0069`) ---

    /// <summary>
    /// An argument that names the parameter it is bound to resolves to the caller's variable.
    /// </summary>
    /// <remarks>
    /// <para>
    /// `RuneThunk.CallerScopes` was `null` both for a leaky rune *and* for a sealed one called
    /// with no visible scopes, and evaluation read that single `null` as "leaky" — evaluate in
    /// the current scope. The current scope is the one holding the rune's parameters, so `$count`
    /// passed to a parameter named `count` found the thunk for itself and re-entered forever.
    /// </para>
    /// <para>
    /// It did not fail an assertion. It overflowed the stack and aborted the whole test run,
    /// twice, which is why the sealed flag is now recorded rather than inferred.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Sealed_rune_argument_naming_its_own_parameter_reads_the_callers_variable()
    {
        var engine = ShellEngine.CreateFullShell();
        var results = await engine.ExecuteToListAsync("""
            rune do-times(count, body) {
                for i in (1..$count) { $body }
            }
            var count = 4
            var n = 0
            do-times $count { $n = $n + 1 }
            echo $n
            """);

        Assert.Equal("4", results[^1]?.ToString());
    }

    /// <summary>A sealed rune's own declarations stay invisible to the caller.</summary>
    /// <remarks>
    /// The control for the test above. Recording the sealed flag would be worth nothing if it
    /// were recorded wrongly — a sealed rune read as leaky leaks, and this is what says so.
    /// </remarks>
    [Fact]
    public async Task Sealed_rune_does_not_leak_its_declarations()
    {
        var engine = ShellEngine.CreateFullShell();
        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await engine.ExecuteToListAsync("""
                rune seal-it(x) {
                    var hidden = $x
                }
                seal-it 7
                echo $hidden
                """));
    }

    /// <summary>A leaky rune still writes into the caller's scope.</summary>
    [Fact]
    public async Task Leaky_rune_still_leaks_its_declarations()
    {
        var engine = ShellEngine.CreateFullShell();
        var results = await engine.ExecuteToListAsync("""
            leaky rune bind-it(x) {
                var bound = $x
            }
            bind-it 7
            echo $bound
            """);

        Assert.Equal("7", results[^1]?.ToString());
    }

    // --- Constant folding is per call site, not per rune body (`TOAST-0071`) ---

    /// <summary>An operator over a rune parameter answers for the call site it is in.</summary>
    /// <remarks>
    /// <para>
    /// Expanding a rune substitutes the argument's *syntax*, so `not $c` against a literal
    /// argument becomes foldable — and folding stamps its answer onto the syntax node as well
    /// as the bound tree, because the interpreter reads that stamp to skip re-evaluating. The
    /// rune body's AST is shared by every expansion, so the stamp from one call site was
    /// handed to the next.
    /// </para>
    /// <para>
    /// The give-away is that `false` then `true` printed `false false` — neither call's own
    /// answer repeated, but the *last* fold answering both.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("not", "rune r(c) { echo (not $c) }\nr false\nr true", "True", "False")]
    [InlineData("equality", "rune r(n) { echo ($n == 0) }\nr 0\nr 5", "True", "False")]
    [InlineData("arithmetic", "rune r(n) { echo ($n + 1) }\nr 2\nr 4", "3", "5")]
    public async Task Folded_operand_answers_for_its_own_call_site(
        string what, string source, string first, string second)
    {
        var engine = ShellEngine.CreateFullShell();
        var results = await engine.ExecuteToListAsync(source);

        Assert.Equal(first, results[^2]?.ToString());
        Assert.Equal(second, results[^1]?.ToString());
        Assert.NotEqual(results[^2]?.ToString(), results[^1]?.ToString());
        _ = what;
    }

    /// <summary>The defect in condition position: the wrong arm runs, not just a wrong value.</summary>
    [Fact]
    public async Task Folded_condition_runs_the_arm_belonging_to_its_call_site()
    {
        var engine = ShellEngine.CreateFullShell();
        var results = await engine.ExecuteToListAsync("""
            rune unless-it(c, body) {
                if (not $c) { $body }
            }
            unless-it true  { echo "A" }
            unless-it false { echo "B" }
            """);

        Assert.Equal(new object[] { "B" }, results);
    }

    /// <summary>Folding outside a rune is untouched.</summary>
    /// <remarks>
    /// The control. Suppressing the stamp everywhere would pass every test above and quietly
    /// cost the interpreter its constant folding, which nothing else here would notice.
    /// </remarks>
    [Fact]
    public async Task Constant_folding_outside_a_rune_still_happens()
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);
        var parse = engine.Parse("echo (3 + 4)", "<fold>");
        _ = Tosh.Language.Binding.Lowerer.Lower(parse, runtime.Commands);

        var stamped = parse.Pipeline.Stages
            .OfType<Tosh.Language.Parsing.CommandSyntax>()
            .SelectMany(command => command.Arguments)
            .OfType<Tosh.Language.Parsing.OperatorArgumentSyntax>()
            .Any(op => op.FoldedConstant is not null);

        Assert.True(stamped, "a constant expression outside a rune should still be stamped.");
    }

    // --- Recursive expansion is bounded (`TOAST-0069`) ---

    /// <summary>A rune that expands forever reports the recursion limit.</summary>
    /// <remarks>
    /// A recursive *function* already reported `tosh.runtime.recursion_limit_exceeded`; rune
    /// expansion was simply not one of the paths the depth guard covered, so it overflowed the
    /// stack instead — which does not throw, cannot be caught, and takes the process with it.
    /// These assertions can only be written *because* it now throws.
    /// </remarks>
    [Theory]
    [InlineData("direct", "rune spin(b) { spin $b }\nspin { echo \"x\" }")]
    [InlineData("mutual", "rune ping(b) { pong $b }\nrune pong(b) { ping $b }\nping { echo \"x\" }")]
    public async Task Runaway_rune_expansion_reports_the_recursion_limit(string what, string source)
    {
        var engine = ShellEngine.CreateFullShell();
        var thrown = await Assert.ThrowsAsync<ToshDiagnosticException>(
            async () => await engine.ExecuteToListAsync(source));

        Assert.Contains(thrown.Diagnostics, d => d.Code == "tosh.runtime.recursion_limit_exceeded");
        _ = what;
    }

    /// <summary>Nesting that terminates is not mistaken for runaway expansion.</summary>
    /// <remarks>
    /// The control. A guard that refused every nested expansion would satisfy the theory above
    /// and make runes useless one level deep.
    /// </remarks>
    [Fact]
    public async Task Deep_but_finite_rune_nesting_still_runs()
    {
        var engine = ShellEngine.CreateFullShell();
        var results = await engine.ExecuteToListAsync("""
            rune a(b) { $b }
            a { a { a { a { a { echo "deep" } } } } }
            """);

        Assert.Equal(new object[] { "deep" }, results);
    }

    // --- A block argument runs where it was written (`TOAST-0072`) ---

    /// <summary>A block forwarded through a second rune keeps its own meaning.</summary>
    /// <remarks>
    /// <para>
    /// The block path of `EvaluateRuneThunkAsync` ignored `CallerScopes` and ran in whatever
    /// scope was current — inside an expansion, that is the rune's own parameter scope. So a
    /// block written in `f` and forwarded through `t` saw *`t`'s* `b` instead of `f`'s.
    /// </para>
    /// <para>
    /// `rune f(b) { t { t $b } }` therefore re-entered `t` forever. It printed nothing at all
    /// before the sealed-scope work, and reported the recursion limit after — a nested macro
    /// calling another macro has never worked.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("one hop", "rune t(b) { $b }\nrune f(b) { t $b }\nf { echo \"z\" }", 1)]
    [InlineData("two hops", "rune t(b) { $b }\nrune f(b) { t { t $b } }\nf { echo \"z\" }", 1)]
    [InlineData("doubling twice", "rune tw(b) { $b\n$b }\nrune fr(b) { tw { tw $b } }\nfr { echo \"z\" }", 4)]
    public async Task A_block_argument_forwarded_through_a_rune_keeps_its_own_scope(
        string what, string source, int expected)
    {
        var engine = ShellEngine.CreateFullShell();
        var results = await engine.ExecuteToListAsync(source);

        // A rune body that yields more than one value hands back an array, and nesting two of
        // them nests the arrays, so the "z"s are not all at the top level.
        static IEnumerable<object?> Flatten(IEnumerable<object?> values)
        {
            foreach (var value in values)
            {
                if (value is object?[] nested)
                {
                    foreach (var inner in Flatten(nested))
                    {
                        yield return inner;
                    }
                }
                else
                {
                    yield return value;
                }
            }
        }

        Assert.Equal(expected, Flatten(results).Count(r => r?.ToString() == "z"));
        _ = what;
    }

    /// <summary>A block still reaches the caller's variables.</summary>
    /// <remarks>
    /// The control. Running the block in the caller's scope is only right if the caller's
    /// scope is where its assignments land — a fix that isolated the block instead would
    /// satisfy the theory above and break every accumulating macro.
    /// </remarks>
    [Fact]
    public async Task A_block_argument_still_mutates_the_callers_variables()
    {
        var engine = ShellEngine.CreateFullShell();
        var results = await engine.ExecuteToListAsync("""
            rune do-twice(b) {
                $b
                $b
            }
            var n = 0
            do-twice { $n = $n + 1 }
            echo $n
            """);

        Assert.Equal("2", results[^1]?.ToString());
    }
}
