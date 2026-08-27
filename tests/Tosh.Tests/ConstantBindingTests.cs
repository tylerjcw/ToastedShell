using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// <c>const</c> is an immutable binding, and holds as one — <c>TS-P1-12</c>.
/// </summary>
/// <remarks>
/// <para>
/// The item was filed as "<c>const</c> accepts arbitrary runtime pipelines and behaves as a
/// readonly binding rather than a constant", with a plan to enforce constant-expression rules and
/// hand runtime immutability to <c>let</c>. Two things settled it differently.
/// </para>
/// <para>
/// First, <c>let</c> is not available: it is already the comprehension binding keyword
/// (<c>[$y for x in $xs let y = … where …]</c>), specified and implemented. Second, the decision
/// was to keep the binding form and correct the specification instead — <c>const StartedAt =
/// (date)</c> says exactly what it means, and nothing in the implementation ever folded a constant
/// at its use sites the way the specification claimed. A compile-time constant remains possible
/// later under its own keyword.
/// </para>
/// <para>
/// That makes the real defect a narrower one, and it was found by probing rather than by reading:
/// reassignment was refused but <i>redeclaration</i> was not. <c>const X = 5</c> followed by
/// <c>var X = 6</c> silently replaced the constant with a mutable binding that then accepted
/// assignment — so the guarantee could be stepped around by anyone who wrote one extra line.
/// </para>
/// </remarks>
public sealed class ConstantBindingTests
{
    private static async Task<string> RunAsync(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);
        var results = await engine.ExecuteToListAsync(source);
        return string.Join(",", results.Select(value => value?.ToString() ?? "null"));
    }

    private static async Task<string> ErrorFor(string source)
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(async () => await RunAsync(source));
        return error.Message;
    }

    // ── What a constant binds ──────────────────────────────────────────────────

    [Theory]
    [InlineData("const MaxRetries = 3\n$MaxRetries", "3")]
    [InlineData("const AppName = \"TōSh\"\n$AppName", "TōSh")]
    [InlineData("const Doubled = (2 * 3)\n$Doubled", "6")]
    public async Task A_constant_binds_the_value_of_its_initialiser(string source, string expected)
    {
        Assert.Equal(expected, await RunAsync(source));
    }

    [Fact]
    public async Task An_initialiser_may_be_any_expression()
    {
        // The behaviour the specification used to forbid and the implementation always allowed.
        // Kept deliberately: the alternative was rejecting it with no replacement spelling.
        Assert.Equal("5", await RunAsync("var v = 5\nconst X = $v\n$X"));
    }

    [Fact]
    public async Task A_constant_is_visible_in_nested_scopes()
    {
        Assert.Equal("3", await RunAsync("const X = 3\nif (true) { $X }"));
    }

    // ── It cannot be reassigned ────────────────────────────────────────────────

    [Theory]
    [InlineData("const X = 5\n$X = 6")]
    [InlineData("const X = 5\n$X += 1")]
    [InlineData("const X = 5\nfunc f() { $X = 6 }\nf")]
    [InlineData("const X = 5\nif (true) { $X = 6 }")]
    public async Task Assignment_to_a_constant_is_refused(string source)
    {
        Assert.Contains("constant", await ErrorFor(source), StringComparison.OrdinalIgnoreCase);
    }

    // ── Nor redeclared, which is what was missing ──────────────────────────────

    [Theory]
    [InlineData("const X = 5\nconst X = 6")]
    [InlineData("const X = 5\nvar X = 6")]
    public async Task Redeclaring_a_constant_in_the_same_scope_is_refused(string source)
    {
        Assert.Contains("redeclare", await ErrorFor(source), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_route_that_laundered_a_constant_into_a_variable_is_closed()
    {
        // The reason redeclaration matters rather than being a style question: this sequence used
        // to answer 7, having converted a constant into something assignable in one step.
        Assert.Contains(
            "redeclare",
            await ErrorFor("const X = 5\nvar X = 6\n$X = 7"),
            StringComparison.OrdinalIgnoreCase);
    }

    // ── Shadowing is not redeclaration ─────────────────────────────────────────

    [Fact]
    public async Task An_inner_scope_may_bind_the_same_name()
    {
        Assert.Equal("6,5", await RunAsync(
            """
            const X = 5
            if (true) {
                const X = 6
                $X
            }
            $X
            """));
    }

    [Fact]
    public async Task The_outer_constant_survives_an_inner_binding()
    {
        Assert.Equal("5", await RunAsync("const X = 5\nif (true) { const X = 6 }\n$X"));
    }

    [Fact]
    public async Task A_function_body_may_bind_the_same_name()
    {
        Assert.Equal("6,5", await RunAsync(
            """
            const X = 5
            func f() { var X = 6
                return $X }
            f
            $X
            """));
    }

    [Theory]
    // Only a *constant* is protected. Redeclaring an ordinary variable stays legal either way,
    // which is what confines the new rule to the guarantee it exists to keep.
    [InlineData("var V = 1\nvar V = 2\n$V", "2")]
    [InlineData("var V = 1\nconst V = 2\n$V", "2")]
    public async Task Redeclaring_an_ordinary_variable_is_unaffected(string source, string expected)
    {
        Assert.Equal(expected, await RunAsync(source));
    }

    // ── The binding is immutable; the value need not be ────────────────────────

    [Theory]
    // Documented rather than changed. Freezing the value would be a different feature, and a much
    // larger one — every collection and record would need an immutable form.
    [InlineData("const R = {| a = 1 |}\n$R.a = 2\n$R.a", "2")]
    [InlineData("const L = [1, 2, 3]\n$L[0] = 9\n$L[0]", "9")]
    public async Task Contents_of_a_constant_value_may_still_change(string source, string expected)
    {
        Assert.Equal(expected, await RunAsync(source));
    }

    [Fact]
    public async Task Rebinding_a_constant_record_is_still_refused()
    {
        // The distinction that makes the case above coherent: the record may change, the name may
        // not be pointed at a different one.
        Assert.Contains(
            "constant",
            await ErrorFor("const R = {| a = 1 |}\n$R = {| a = 2 |}"),
            StringComparison.OrdinalIgnoreCase);
    }
}
