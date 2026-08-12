using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// A struct's declared constructor is consulted, and its properties are writable —
/// <c>TS-P2-83</c>.
/// </summary>
/// <remarks>
/// <para>
/// <c>struct S { S(x: int) { … } }</c> then <c>new S(9)</c> reported "Struct 'S' expects 0
/// argument(s) but received 1". The switch that builds a struct handled properties and methods
/// and had no case for a constructor at all, so one was parsed and then dropped on the floor —
/// the declaration existed and did nothing.
/// </para>
/// <para>
/// Carrying it exposed a second gap underneath: <c>TrySetMember</c> knew only <c>Fields</c>, so
/// <c>$s.X = 9</c> fell through to reflection and reported the member missing on a struct that
/// reads <c>$s.X</c> perfectly well. That is the same omission <c>GetMembers</c> carried until it
/// was fixed to list properties beside fields — introspection and behaviour disagreeing, one
/// method over. A constructor body could not have run without it.
/// </para>
/// </remarks>
public sealed class StructConstructorTests : IClassFixture<ToshRuntimeFixture>
{
    private readonly ToshRuntime _runtime;

    public StructConstructorTests(ToshRuntimeFixture fixture) => _runtime = fixture.Runtime;

    private async Task<object?> EvalAsync(string script)
    {
        var engine = new ToshEngine(_runtime);
        return (await engine.ExecuteToListAsync(script)).LastOrDefault();
    }

    private async Task<string> FailureAsync(string script)
    {
        var engine = new ToshEngine(_runtime);
        var exception = await Assert.ThrowsAnyAsync<Exception>(
            async () => await engine.ExecuteToListAsync(script));

        return exception is ToshDiagnosticException diagnostic
            ? string.Join(" ", diagnostic.Diagnostics.Select(d => d.Title))
            : exception.Message;
    }

    // ── the declared constructor runs ──────────────────────────────────────────

    [Theory]
    [InlineData("struct S { prop X: int = 0\nS(x: int) { $this.X = $x } }\n(new S(9)).X", 9)]
    [InlineData("struct S { prop A: int = 0\nprop B: int = 0\n" +
                "S(a: int, b: int) { $this.A = $a\n$this.B = $b } }\n(new S(1, 2)).B", 2)]
    // The body is ordinary code, so it may compute rather than merely copy.
    [InlineData("struct S { prop X: int = 0\nS(x: int) { $this.X = ($x * 2) } }\n(new S(4)).X", 8)]
    public async Task A_declared_constructor_builds_the_value(string script, int expected)
    {
        Assert.Equal(expected, Convert.ToInt32(await EvalAsync(script)));
    }

    // ── property assignment, which the constructor needed ──────────────────────

    [Fact]
    public async Task A_fluid_structs_property_is_writable()
    {
        Assert.Equal(9, Convert.ToInt32(await EvalAsync(
            "fluid struct S { prop X: int = 5 }\nvar s = new S()\n$s.X = 9\n$s.X")));
    }

    [Fact]
    public async Task A_fluid_structs_property_is_writable_from_a_method()
    {
        Assert.Equal(9, Convert.ToInt32(await EvalAsync(
            "fluid struct S { prop X: int = 5\nfunc bump() { $this.X = 9\nreturn $this.X } }\n" +
            "var s = new S()\n$s.bump()")));
    }

    [Fact]
    public async Task An_immutable_structs_property_says_it_is_immutable()
    {
        // It used to report the member missing, which is a different problem entirely and sends
        // the reader looking for a typo rather than at the `fluid` modifier.
        var message = await FailureAsync("struct S { prop X: int = 5 }\nvar s = new S()\n$s.X = 9");

        Assert.Contains("immutable struct 'S'", message, StringComparison.Ordinal);
        Assert.Contains("fluid", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Construction_is_not_mutation()
    {
        // A constructor writing its own properties must be allowed on an immutable struct — it is
        // building the value, not changing one — while a write afterwards is still refused.
        Assert.Equal(9, Convert.ToInt32(await EvalAsync(
            "struct S { prop X: int = 0\nS(x: int) { $this.X = $x } }\n(new S(9)).X")));

        var message = await FailureAsync(
            "struct S { prop X: int = 0\nS(x: int) { $this.X = $x } }\nvar s = new S(9)\n$s.X = 1");

        Assert.Contains("immutable struct 'S'", message, StringComparison.Ordinal);
    }

    // ── the existing forms are untouched ───────────────────────────────────────

    [Theory]
    [InlineData("struct P(x: int) { prop X: int = $x }\n(new P(9)).X", 9)]
    [InlineData("struct Z { prop X: int = 5 }\n(new Z()).X", 5)]
    [InlineData("struct R(a: int, b: int)\nvar r = new R(1, 2)\n$r.b", 2)]
    [InlineData("struct D(x: int) { prop Doubled: int = ($x * 2) }\n(new D(4)).Doubled", 8)]
    public async Task The_field_and_primary_constructor_forms_still_work(string script, int expected)
    {
        Assert.Equal(expected, Convert.ToInt32(await EvalAsync(script)));
    }

    [Fact]
    public async Task A_class_constructor_is_unaffected()
    {
        Assert.Equal(9, Convert.ToInt32(await EvalAsync(
            "class C { prop X: int = 0\nC(x: int) { $this.X = $x } }\n(new C(9)).X")));
    }
}
