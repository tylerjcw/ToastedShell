using Tosh.Language;
using Tosh.Language.Binding;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// `TOAST-0056`: a user-declared math type presents the syntax its domain expects.
/// </summary>
/// <remarks>
/// <para>
/// The runtime already dispatched a zero-argument unary method — <c>TryInvokeClassUnaryOperatorAsync</c>
/// has been wired into the unary evaluator all along — but the type checker warned
/// <c>Unary operator '-' is not compatible with operand type 'Vec'</c> at every use, because it
/// judged unary operands it could not reason about instead of declining to. The binary check has
/// declined since it was written; the two now share the guard.
/// </para>
/// <para>
/// Indexers were "parser-level wired" in the specification's words, which meant the brackets
/// parsed and nothing could answer them. <c>[]</c> and <c>[]=</c> are ordinary operator method
/// names now, so an indexer needs no declaration form of its own.
/// </para>
/// </remarks>
public sealed class UnaryAndIndexerOverloadTests : IClassFixture<ToshRuntimeFixture>
{
    private readonly ToshRuntime _runtime;

    public UnaryAndIndexerOverloadTests(ToshRuntimeFixture fixture) => _runtime = fixture.Runtime;

    private static async Task<string> RunAsync(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);
        var results = await engine.ExecuteToListAsync(source);
        return string.Join(",", results.Select(value => $"{value}"));
    }

    /// <summary>
    /// The operator check is a Preview-lifecycle diagnostic from the type checker, which
    /// runs over the lowered unit rather than as part of parsing.
    /// </summary>
    private IReadOnlyList<ToshDiagnostic> Check(string source)
    {
        var engine = new ToshEngine(_runtime.Language);
        var unit = Lowerer.Lower(engine.Parse(source, "<operator-overload-test>"), _runtime.Commands);
        return TypeChecker.Check(unit);
    }

    private const string Vec = """
        class Vec(x, y, z) {
            shy prop Items = [$x, $y, $z]
            func [](i)     => $this.Items[$i]
            func []=(i, v) { $this.Items[$i] = $v }
            func -()       => (new Vec((0 - $this[0]), (0 - $this[1]), (0 - $this[2])))
            func not()     => ((($this[0] == 0) and ($this[1] == 0)) and ($this[2] == 0))
            func ToString() -> string => $"Vec({$this[0]}, {$this[1]}, {$this[2]})"
        }
        """;

    [Fact]
    public async Task A_class_may_overload_prefix_minus()
    {
        Assert.Equal("Vec(-3, -4, -5)", await RunAsync($"{Vec}\necho (- (new Vec(3, 4, 5))).ToString()"));
    }

    [Theory]
    [InlineData("new Vec(3, 4, 5)", "false")]
    [InlineData("new Vec(0, 0, 0)", "true")]
    public async Task A_class_may_overload_not(string construction, string expected)
    {
        // Interpolated in ToastScript so the assertion is on the rendering a reader sees,
        // rather than on the CLR bool's own ToString.
        Assert.Equal(expected, await RunAsync($"{Vec}\necho $\"{{(not ({construction}))}}\""));
    }

    [Fact]
    public void Overloading_unary_no_longer_warns()
    {
        // The value was always right; the checker called it incompatible anyway.
        Assert.DoesNotContain(
            Check($"{Vec}\nvar n = (- (new Vec(1, 2, 3)))"),
            d => d.Code == "tosh.type.operator");
    }

    [Fact]
    public void An_operator_the_checker_understands_is_still_rejected()
    {
        // Declining to judge unknown types must not mean declining to judge known ones.
        Assert.Contains(
            Check("var s = \"text\"\nvar bad = (- $s)"),
            d => d.Code == "tosh.type.operator");
    }

    [Fact]
    public async Task An_indexer_reads_and_writes()
    {
        Assert.Equal(
            "3,Vec(3, 99, 5)",
            await RunAsync($"{Vec}\nvar v = (new Vec(3, 4, 5))\necho $v[0]\n$v[1] = 99\necho $v.ToString()"));
    }

    [Fact]
    public async Task Compound_assignment_reads_and_writes_through_the_indexer()
    {
        // The write went through `[]=` before the read went through `[]`, so this failed
        // on the read while plain assignment worked — a confusing pair to explain.
        Assert.Equal("100", await RunAsync($"{Vec}\nvar v = (new Vec(3, 99, 5))\n$v[1] += 1\necho $v[1]"));
    }

    [Fact]
    public async Task An_indexer_is_reachable_from_inside_the_class()
    {
        // `$this` is a self-reference wrapper rather than the instance, so the indexer had
        // to be unwrapped to be usable by the type that declares it.
        Assert.Equal("Vec(3, 4, 5)", await RunAsync($"{Vec}\necho (new Vec(3, 4, 5)).ToString()"));
    }

    [Fact]
    public async Task A_class_with_no_indexer_is_still_not_indexable()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);

        await Assert.ThrowsAnyAsync<Exception>(
            () => engine.ExecuteToListAsync("class Plain { prop N = 1 }\necho (new Plain())[0]"));
    }


    // ── TOAST-0117: what a class that declared neither one is told ────────────
    //
    // `EvaluateUnary` expresses `-x` as `0 - x`, deliberately, so every widening and unit
    // rule the binary operators carry applies unchanged. For a class that paid off as
    // "Operator operands 'System.Int32' and 'Plain' are not compatible" — a binary
    // mismatch against an integer written nowhere in the source. The indexer's message
    // named `Tosh.Language.ToshClassInstance`, the CLR class behind every user object.
    // Both now name the class and the member it could declare.

    private const string Plain = "class Plain(x) { prop X = $x\n prop Y = 9 }\nvar p = (new Plain(1))\n";

    [Theory]
    [InlineData("-$p", "tosh.runtime.unary_operator_not_defined", "func -()")]
    [InlineData("+$p", "tosh.runtime.unary_operator_not_defined", "func +()")]
    [InlineData("$p[0]", "tosh.runtime.indexer_not_defined", "func [](i)")]
    public async Task A_missing_operator_names_the_class_and_the_member(
        string expression,
        string expectedCode,
        string expectedMember)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);
        var error = await Assert.ThrowsAnyAsync<ToshDiagnosticException>(
            () => engine.ExecuteToListAsync($"{Plain}{expression}"));

        var diagnostic = Assert.Single(error.Diagnostics, d => d.Code == expectedCode);

        Assert.Contains("Plain", diagnostic.Title, StringComparison.Ordinal);
        Assert.DoesNotContain("Int32", diagnostic.Title, StringComparison.Ordinal);
        Assert.DoesNotContain("ToshClassInstance", diagnostic.Title, StringComparison.Ordinal);
        Assert.Contains(expectedMember, $"{diagnostic.Label} {diagnostic.Help}", StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_index_setter_is_reported_like_the_getter()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);
        var error = await Assert.ThrowsAnyAsync<ToshDiagnosticException>(
            () => engine.ExecuteToListAsync($"{Plain}$p[0] = 5"));

        var diagnostic = Assert.Single(error.Diagnostics, d => d.Code == "tosh.runtime.indexer_not_defined");

        Assert.Contains("Plain", diagnostic.Title, StringComparison.Ordinal);
        Assert.Contains("func []=(i, v)", $"{diagnostic.Label} {diagnostic.Help}", StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_class_is_still_indexed_by_a_member_name()
    {
        // The general path can answer for a class — a name indexes one like a record — so it
        // runs first and only its refusal is rewritten. Reporting before trying would have
        // broken this.
        Assert.Equal("1", await RunAsync($"{Plain}var key = \"X\"\necho $p[$key]"));
    }

    [Fact]
    public async Task Not_still_falls_back_to_truthiness()
    {
        // `not` is left alone: unlike `-`, its fallback is a real answer for any object.
        Assert.Equal("False", await RunAsync($"{Plain}echo (not $p)"));
    }

    [Fact]
    public async Task A_non_class_value_keeps_its_clr_name()
    {
        // The CLR name is what a reader wants for a genuine CLR value; only values that
        // carry a shell type answer with it.
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);
        var error = await Assert.ThrowsAnyAsync<Exception>(
            () => engine.ExecuteToListAsync("var n = 5\necho $n[0]"));

        Assert.Contains("System.Int32", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("var xs = [10, 20, 30]\necho $xs[1]", "20")]
    [InlineData("var d = {% \"k\" => \"v\" %}\necho $d[\"k\"]", "v")]
    public async Task Built_in_indexing_is_unchanged(string source, string expected)
    {
        Assert.Equal(expected, await RunAsync(source));
    }

    // ── `TOAST-0056`: the three limitations, pinned ───────────────────────────
    //
    // The unary and indexer surface landed and the plan item went on describing it as
    // missing, so `AGENTS.md` did too. What is *not* there has the same problem in reverse:
    // nothing held these three shut, so a reader had only the prose's word for it. Each is
    // deliberate or tracked, and each should fail loudly enough that closing it means
    // updating the specification in the same change.

    /// <summary>
    /// An indexer takes one argument.
    /// </summary>
    /// <remarks>
    /// Not an oversight: a comma inside brackets already selects between the three lookup
    /// forms — <c>[value]</c>, <c>[key,]</c> and <c>[,value]</c> — so <c>$m[$i, $j]</c>
    /// collides with a spelling that means something else. Closing it is a decision about
    /// that grammar, not an implementation.
    /// </remarks>
    [Fact]
    public void Multi_argument_indexing_is_refused_by_the_parser()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);
        var parse = engine.Parse("class V { func [](i) => $i }\n(new V())[0, 1]", "<double-index>");

        Assert.Contains(
            parse.Diagnostics,
            diagnostic => diagnostic.Code == "tosh.parser.unsupported_double_index_lookup");
    }

    /// <summary>
    /// Unary resolution does not consult CLR <c>op_*</c> methods, unlike the binary
    /// operators.
    /// </summary>
    /// <remarks>
    /// `TOAST-0051` gave the binary operators a CLR fallback and the unary path never got
    /// one, so the same value answers one and not the other. Asserted as a pair, because the
    /// asymmetry is the finding — a bare failure would read as "TimeSpan does not do
    /// arithmetic", which is not true.
    /// </remarks>
    [Fact]
    public async Task Unary_does_not_fall_back_to_a_clr_operator_although_binary_does()
    {
        Assert.Equal(
            "00:10:00",
            await RunAsync("var a = System.TimeSpan.FromMinutes(5)\n($a + $a).ToString()"));

        await Assert.ThrowsAnyAsync<Exception>(
            () => RunAsync("var a = System.TimeSpan.FromMinutes(5)\n-$a"));
    }

    /// <summary>
    /// Compound assignment is not its own operator: it desugars, so <c>+</c> covers <c>+=</c>
    /// and there is no hook that would let a value type write in place.
    /// </summary>
    [Fact]
    public async Task Compound_assignment_desugars_to_the_binary_form()
    {
        Assert.Equal(
            "3",
            await RunAsync(
                "class V(a) { prop A = $a\n    func +(o) => (new V(($this.A + $o.A))) }\n"
                + "var v = (new V(1))\n$v += (new V(2))\n$v.A"));

        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);
        var parse = engine.Parse("class V { func +=(o) => 99 }", "<compound-overload>");

        Assert.NotEmpty(parse.Diagnostics);
    }
}
