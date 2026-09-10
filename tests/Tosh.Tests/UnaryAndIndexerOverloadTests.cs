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

    [Theory]
    [InlineData("var xs = [10, 20, 30]\necho $xs[1]", "20")]
    [InlineData("var d = {% \"k\" => \"v\" %}\necho $d[\"k\"]", "v")]
    public async Task Built_in_indexing_is_unchanged(string source, string expected)
    {
        Assert.Equal(expected, await RunAsync(source));
    }
}
