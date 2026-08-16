using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// An interpolation hole is parsed once and evaluated every time.
///
/// `TS-P2-121`. A hole was re-parsed from its source text on every evaluation —
/// lexer, parser, binder and lowering pass — so `$"x{$i}"` in a loop re-derived
/// the meaning of `$i` a million times. Interpolation measured 19,562 ns against
/// 232 ns for a string concatenation, the largest outlier in the language; it is
/// now 988 ns.
///
/// The whole risk of the fix is confusing *the program* with *its result*. The
/// parse tree is cached; nothing about the evaluation is. These tests exist to
/// pin that distinction, because a cache that also froze the value would pass
/// any test that interpolated a constant.
/// </summary>
public class InterpolationHoleReuseTests
{
    private static async Task<string> RunAsync(string source)
    {
        var output = new StringWriter();
        var engine = new ToshEngine(ToshRuntime.CreateDefault(output, output));
        await engine.ExecuteToListAsync(source);
        return output.ToString().Replace("\r", "").Trim();
    }

    /// <summary>
    /// The heart of it: the same hole, evaluated repeatedly, tracks the variable.
    /// A cached *result* would print "x1" five times.
    /// </summary>
    [Fact]
    public async Task A_hole_is_re_evaluated_on_every_pass()
        => Assert.Equal("x1 x2 x3 x4 x5", await RunAsync(
            """
            var i = 0
            var parts = ""
            until ($i == 5) {
                $i += 1
                $parts = ($parts + $"x{$i} ")
            }
            writeline $parts.Trim()
            """));

    /// <summary>
    /// A hole with a side effect runs it every time. This is the sharpest form of
    /// the same question: the counter must reach 3, not 1.
    /// </summary>
    [Fact]
    public async Task A_side_effect_inside_a_hole_happens_every_time()
        => Assert.Equal("3", await RunAsync(
            """
            var calls = 0
            func bump() { $calls += 1
                return $calls }
            var i = 0
            until ($i == 3) { $i += 1
                var ignored = $"{bump()}" }
            writeline $calls
            """));

    /// <summary>
    /// A hole reading a variable that is rebound between evaluations sees the new
    /// binding, not the one in scope when it was first parsed.
    /// </summary>
    [Fact]
    public async Task A_hole_sees_the_current_binding_not_the_first()
        => Assert.Equal("a|b", await RunAsync(
            """
            func show(v) => $"{$v}"
            writeline (show("a") + "|" + show("b"))
            """));

    /// <summary>
    /// Several holes in one string keep their own programs — caching on the wrong
    /// key would give them all the first hole's.
    /// </summary>
    [Fact]
    public async Task Each_hole_in_a_string_keeps_its_own_program()
        => Assert.Equal("1-2-3", await RunAsync(
            """
            var a = 1
            var b = 2
            var c = 3
            writeline $"{$a}-{$b}-{$c}"
            """));

    /// <summary>
    /// An expression hole, not just a variable — the program is a whole statement,
    /// which is why the hole goes through the statement path at all.
    /// </summary>
    [Theory]
    [InlineData("var n = 4\nwriteline $\"{($n * 2)}\"", "8")]
    [InlineData("var xs = [1, 2, 3]\nwriteline $\"{($xs | count)}\"", "3")]
    [InlineData("func f(x) => ($x + 1)\nwriteline $\"{f(1)}\"", "2")]
    public async Task A_hole_may_be_a_whole_expression(string source, string expected)
        => Assert.Equal(expected, await RunAsync(source));

    /// <summary>
    /// A hole that cannot be parsed still reports, and reports on *every*
    /// evaluation rather than only the first — only a successful preparation is
    /// kept, so a failing one must not be silently swallowed the second time.
    /// </summary>
    [Fact]
    public async Task A_hole_that_does_not_parse_reports_every_time()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        for (var attempt = 0; attempt < 2; attempt++)
        {
            await Assert.ThrowsAnyAsync<Exception>(
                () => engine.ExecuteToListAsync("writeline $\"{ ( }\""));
        }
    }

    /// <summary>
    /// The hole is prepared at its first *evaluation*, not when the enclosing string
    /// is parsed, so a bad hole on a path never taken stays silent. Preparing every
    /// hole eagerly would change when errors surface, which a performance fix has no
    /// business doing.
    /// </summary>
    [Fact]
    public async Task A_bad_hole_on_an_untaken_branch_stays_silent()
        => Assert.Equal("safe", await RunAsync(
            """
            if (false) { writeline $"{ ( }" }
            writeline "safe"
            """));

    /// <summary>
    /// One parse tree, evaluated by two engines through the public
    /// <c>EvaluateAsync(BoundUnit)</c> seam — the one way a hole node can reach more
    /// than one engine, and so the reason the cache records which engine prepared it.
    /// </summary>
    /// <remarks>
    /// This covers the shared-unit path working; it does **not** prove the engine key
    /// is load-bearing. Removing the key leaves this passing, because a hole's parse
    /// tree is only engine-specific when the two engines' registries would make the
    /// same text *parse* differently, and no such case could be constructed here.
    /// The key is kept as the cheap, obviously-correct option rather than a measured
    /// one, and is recorded that way instead of being covered by a test that only
    /// looks like it covers it.
    /// </remarks>
    [Fact]
    public async Task One_parse_tree_can_be_evaluated_by_two_engines()
    {
        var shared = new ToshEngine(ToshRuntime.CreateDefault())
            .Parse("writeline $\"[{name()}]\"");
        var unit = Tosh.Language.Binding.Lowerer.Lower(shared, ToshRuntime.CreateDefault().Commands);

        static async Task<string> EvaluateAsync(Tosh.Compiler.IR.BoundUnit unit, string definition)
        {
            var output = new StringWriter();
            var engine = new ToshEngine(ToshRuntime.CreateDefault(output, output));
            await engine.ExecuteToListAsync(definition);

            await foreach (var _ in engine.EvaluateAsync(unit)) { }

            return output.ToString().Replace("\r", string.Empty).Trim();
        }

        Assert.Equal("[one]", await EvaluateAsync(unit, "func name() => \"one\""));
        Assert.Equal("[two]", await EvaluateAsync(unit, "func name() => \"two\""));
    }

    /// <summary>
    /// A command inside a hole still has its output captured rather than inherited
    /// — `TS-P1-32`, which is the reason the hole is evaluated with
    /// `outputIsCaptured: true` and had to keep being.
    /// </summary>
    [Fact]
    public async Task A_command_in_a_hole_is_still_captured()
        => Assert.Equal("[hello]", await RunAsync("writeline $\"[{echo \"hello\"}]\""));
}
