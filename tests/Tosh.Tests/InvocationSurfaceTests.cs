using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// Every kind of callable, at every kind of call site.
///
/// The invocation surface had been repaired one anecdote at a time — `TS-P2-01`
/// for `f() + 1`, `TS-P2-114` for a sibling in an interpolation hole, and this
/// round for a callable held in a property (`TS-P2-93`) and a method call on a
/// static property's value (`TS-P2-92`). Each was found by someone tripping over
/// it, which is a poor way to discover that a surface has holes.
///
/// This is the matrix instead: callable kind against call site, so a hole is
/// found by running the table rather than by hitting it in real work. Building it
/// found no *new* failures beyond the three already filed — which is worth
/// knowing, because the alternative reading was that the surface was riddled.
///
/// <para>The one methodological trap, recorded because it nearly published a
/// wrong result: run this against the <em>current build</em>. The first matrix
/// run used the installed <c>tosh</c>, which predated the day's fixes, and
/// reported as broken several cells that were already repaired.</para>
/// </summary>
public class InvocationSurfaceTests
{
    private static async Task<string> RunAsync(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync(source);
        return string.Join(",", results.Select(value => value?.ToString() ?? "null"));
    }

    /// <summary>
    /// Every callable here is the identity on its argument, so any cell that
    /// resolves at all must answer 42. A cell that returns something else has
    /// dispatched to the wrong thing, which a "did not throw" assertion would
    /// have missed.
    /// </summary>
    private const string Fixture = """
        func TopLevel(x: int) -> int => $x

        module M {
            export func Exported(x: int) -> int => $x
            func Sibling(x: int) -> int => $x
            export func FromSibling() -> int => (Sibling(42))
            export func FromSiblingHole() -> string => $"{Sibling(42)}"
        }

        class C {
            prop Fn = func(x) => $x
            shared prop SFn = func(x) => $x
            shared prop Text = "hello"
            prop IText = "world"
            func Method(x: int) -> int => $x
            static func Static(x: int) -> int => $x
        }

        var lambda = func(x) => $x
        var obj = new C()

        """;

    [Theory]
    // Top-level function.
    [InlineData("TopLevel 42", "42")]
    [InlineData("(TopLevel(42))", "42")]
    [InlineData("$\"{TopLevel(42)}\"", "42")]
    [InlineData("var r = &TopLevel\n$r(42)", "42")]
    // Module function, from inside the module and from outside it.
    [InlineData("M.FromSibling()", "42")]
    [InlineData("M.FromSiblingHole()", "42")]
    [InlineData("(M.Exported(42))", "42")]
    [InlineData("$\"{M.Exported(42)}\"", "42")]
    // Methods.
    [InlineData("($obj.Method(42))", "42")]
    [InlineData("$\"{$obj.Method(42)}\"", "42")]
    [InlineData("(C.Static(42))", "42")]
    [InlineData("$\"{C.Static(42)}\"", "42")]
    // A lambda held in a variable.
    [InlineData("($lambda(42))", "42")]
    [InlineData("$\"{$lambda(42)}\"", "42")]
    // `TS-P2-93` — a callable held in a property, on both sides of the class.
    [InlineData("($obj.Fn(42))", "42")]
    [InlineData("(C.SFn(42))", "42")]
    [InlineData("var f = $obj.Fn\n$f(42)", "42")]
    public async Task Every_callable_answers_at_every_call_site(string body, string expected)
        => Assert.Equal(expected, await RunAsync(Fixture + body));

    /// <summary>
    /// `TS-P2-94`. `&amp;` reused the *command-name* rule, which forbids a dot, so
    /// `&amp;M.Exported` and `&amp;C.Static` failed the guard and were reported as a
    /// stray background operator. The rule was written out in three places — the
    /// argument parser, the pipeline-start check, and `LiteParser`'s structural
    /// pass — and all three had to agree or the same text would parse differently
    /// depending on which pass reached it first.
    /// </summary>
    [Theory]
    [InlineData("var f = &TopLevel\n$f(42)", "42")]
    [InlineData("var f = &M.Exported\n$f(42)", "42")]
    [InlineData("var f = &M.Deep.Down\n$f(42)", "42")]
    [InlineData("var f = &C.Static\n$f(42)", "42")]
    [InlineData("var f = &C.Pair\n$f(20, 22)", "42")]
    public async Task A_function_reference_reaches_qualified_names(string body, string expected)
        => Assert.Equal(expected, await RunAsync(ReferenceFixture + body));

    /// <summary>
    /// The reference stands for the whole overload set, not one signature, so
    /// arity is settled at call time by the ordinary dispatcher rather than
    /// captured here. A second, weaker resolver in the reference is the
    /// `TS-P1-24` failure mode.
    /// </summary>
    [Fact]
    public async Task A_static_reference_still_resolves_overloads_at_call_time()
        => Assert.Equal("42,42", await RunAsync(
            ReferenceFixture +
            """
            var f = &C.Over
            $f(42)
            $f(20, 22)
            """));

    [Fact]
    public async Task A_reference_to_a_name_that_does_not_exist_is_reported()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var exception = await Assert.ThrowsAnyAsync<Exception>(
            () => engine.ExecuteToListAsync(ReferenceFixture + "var f = &C.Nope\n$f(1)"));

        Assert.Contains("Nope", exception.Message);
    }

    private const string ReferenceFixture = """
        func TopLevel(x: int) -> int => $x

        module M {
            export func Exported(x: int) -> int => $x
            export module Deep { export func Down(x: int) -> int => $x }
        }

        class C {
            static func Static(x: int) -> int => $x
            static func Pair(a: int, b: int) -> int => ($a + $b)
            static func Over(x: int) -> int => $x
            static func Over(a: int, b: int) -> int => ($a + $b)
        }

        """;

    /// <summary>
    /// `TS-P2-92`. A static property's value could be *read* — `C.Text.Length`
    /// answered 5 — but not called, so `C.Text.ToUpper()` reported "Unable to
    /// resolve .NET access path". The planner accepted exactly two segments, so a
    /// member chain through a static property had nowhere to go.
    /// </summary>
    [Theory]
    [InlineData("(C.Text.ToUpper())", "HELLO")]
    [InlineData("(C.Text.Length)", "5")]
    [InlineData("($obj.IText.ToUpper())", "WORLD")]
    public async Task A_member_chain_through_a_property_can_be_called(string body, string expected)
        => Assert.Equal(expected, await RunAsync(Fixture + body));

    /// <summary>
    /// The non-regression for the same change: a CLR static still resolves, with
    /// and without a chain.
    /// </summary>
    [Theory]
    [InlineData("(Math.Max(1, 9))", "9")]
    [InlineData("(Math.PI.ToString())", "3.141592653589793")]
    public async Task A_clr_static_call_is_unaffected(string body, string expected)
        => Assert.Equal(expected, await RunAsync(body));

    /// <summary>
    /// The precedence control for `TS-P2-93`: the property fallback runs only
    /// after every method candidate has failed, so a real method of the same name
    /// still wins. Without this, adding a property could silently steal calls from
    /// a method that already worked.
    /// </summary>
    [Fact]
    public async Task A_real_method_still_wins_over_a_property_of_the_same_name()
        => Assert.Equal("6", await RunAsync(
            """
            class D {
                prop Both = func(x) => 999
                func Both(x: int) -> int => ($x + 5)
            }
            (new D()).Both(1)
            """));

    /// <summary>
    /// And a member that is genuinely absent must still be reported rather than
    /// swallowed by the new fallback.
    /// </summary>
    [Fact]
    public async Task A_missing_member_is_still_reported()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var exception = await Assert.ThrowsAnyAsync<Exception>(
            () => engine.ExecuteToListAsync(Fixture + "($obj.Nope(1))"));

        Assert.Contains("Nope", exception.Message);
    }
}
