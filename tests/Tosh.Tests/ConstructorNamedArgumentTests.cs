using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// A <c>new</c> expression takes named arguments — <c>TS-P2-21</c>.
/// </summary>
/// <remarks>
/// <para>
/// <c>new D(1, b = 7)</c> failed <em>while parsing</em>, with
/// <c>tosh.parser.assignment_in_predicate</c> — a message about predicates for a call containing
/// no predicate — so the runtime binder was never reached and <c>TS-P1-06</c>'s constructor
/// validation was unreachable behind it.
/// </para>
/// <para>
/// The cause was the shape this programme keeps finding. The named-argument test and read were
/// written out twice, for method calls and for record-style literals, and the third argument list
/// — <c>new</c>'s — simply never got a copy. It lives in one place now. Underneath, records and
/// structs then bound their fields strictly by position, so the wrapper was assigned whole and
/// reported "'R.Qty' produced a value that could not be converted to 'int'"; that binder was also
/// duplicated between the two, and is likewise shared now.
/// </para>
/// </remarks>
public sealed class ConstructorNamedArgumentTests : IClassFixture<ToshRuntimeFixture>
{
    private readonly ToshRuntime _runtime;

    public ConstructorNamedArgumentTests(ToshRuntimeFixture fixture) => _runtime = fixture.Runtime;

    private async Task<object?> EvalAsync(string script)
    {
        var engine = new ToshEngine(_runtime);
        return (await engine.ExecuteToListAsync(script)).LastOrDefault();
    }

    private const string ClassWithConstructor =
        """
        class D {
            prop A: int = 0
            prop B: int = 0
            D(a: int, b: int) { $this.A = $a
                                $this.B = $b }
        }
        """;

    // ── the form parses and binds ──────────────────────────────────────────────

    [Theory]
    [InlineData("var d = new D(1, b = 7)\n$d.B", 7)]
    [InlineData("var d = new D(a = 1, b = 7)\n$d.A", 1)]
    [InlineData("var d = new D(b = 7, a = 1)\n$d.B", 7)]
    [InlineData("var d = new D(1, 7)\n$d.B", 7)]
    public async Task A_class_constructor_takes_named_arguments(string tail, int expected)
    {
        Assert.Equal(expected, Convert.ToInt32(await EvalAsync($"{ClassWithConstructor}\n{tail}")));
    }

    [Theory]
    [InlineData("record R(Name: string, Qty: int)\n(new R(\"w\", Qty = 5)).Qty", 5)]
    [InlineData("record R(Name: string, Qty: int)\n(new R(Qty = 5, Name = \"w\")).Qty", 5)]
    // Case-insensitive on the name, matching the class-constructor binder this is modelled on.
    [InlineData("record R(Name: string, Qty: int)\n(new R(\"w\", qty = 5)).Qty", 5)]
    [InlineData("record R(Name: string, Qty: int)\n(new R(\"w\", 5)).Qty", 5)]
    [InlineData("struct P(x: int) { prop X: int = $x }\n(new P(x = 9)).X", 9)]
    [InlineData("class C(x: int) { prop X: int = $x }\n(new C(x = 9)).X", 9)]
    public async Task Records_structs_and_primary_constructors_take_them_too(string script, int expected)
    {
        Assert.Equal(expected, Convert.ToInt32(await EvalAsync(script)));
    }

    // ── the binder's diagnostics apply ─────────────────────────────────────────

    [Fact]
    public async Task An_unknown_name_names_the_fields_that_exist()
    {
        var exception = await Assert.ThrowsAnyAsync<Exception>(
            async () => await EvalAsync("record R(Name: string, Qty: int)\nnew R(\"w\", Nope = 5)"));

        Assert.Contains("has no field named 'Nope'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Name, Qty", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_repeated_name_is_reported_rather_than_overwritten()
    {
        // Keeping the last one silently left the earlier field looking unsupplied, so this
        // complained that `Name` was missing — a field the caller never mentioned.
        var exception = await Assert.ThrowsAnyAsync<Exception>(
            async () => await EvalAsync("record R(Name: string, Qty: int)\nnew R(Qty = 5, Qty = 6)"));

        Assert.Contains("supplied more than once", exception.Message, StringComparison.Ordinal);
        Assert.Contains("'Qty'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_genuine_assignment_inside_a_constructor_call_is_still_rejected()
    {
        // `$x = 5` is an assignment, not a named argument — the `$` says so — and must keep
        // failing rather than being read as a name.
        var exception = await Assert.ThrowsAnyAsync<Exception>(
            async () => await EvalAsync($"{ClassWithConstructor}\nvar x = 1\nnew D($x = 5, 2)"));

        Assert.NotNull(exception);
    }

    // ── what must not change ───────────────────────────────────────────────────

    [Theory]
    [InlineData("class K { func m(a: int, b: int) { return $b } }\nvar k = new K()\n$k.m(1, b = 7)", 7)]
    [InlineData("({| a = 1, b = 2 |}).b", 2)]
    public async Task The_other_argument_lists_are_unaffected(string script, int expected)
    {
        Assert.Equal(expected, Convert.ToInt32(await EvalAsync(script)));
    }
}
