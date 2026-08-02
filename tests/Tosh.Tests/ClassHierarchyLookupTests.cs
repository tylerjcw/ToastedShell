using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// What a class inherits from its base — statics, required members, overload sets —
/// <c>TS-P1-09</c>.
/// </summary>
/// <remarks>
/// <para>
/// The item listed five losses in hierarchy lookup. Probing each one first changed the list:
/// </para>
/// <list type="bullet">
/// <item>
/// <b>Generic bindings — did not reproduce.</b> The specification's own <c>Point2D</c> example,
/// three-level chains, constrained bases and reordered type parameters all resolve correctly.
/// The cases are kept here as characterization, since the claim will otherwise be re-filed.
/// </item>
/// <item>
/// <b>Statics — worse than "partial": they were not inherited at all.</b> Every instance lookup
/// walked <c>BaseClass</c>; none of the four static entry points did, so <c>D.s()</c> failed with
/// "Static method 's' was not found on class 'D'" while <c>B.s()</c> worked.
/// </item>
/// <item>
/// <b><c>vital</c> — not validated when inherited.</b> Construction checked <c>Properties</c>,
/// which is what the class itself declares, so <c>class D extends B { }</c> could be built with
/// B's required property left unset.
/// </item>
/// <item>
/// <b>Overload sets — lost in two separate places.</b> The declaration check asked only whether
/// the *name* existed in the hierarchy, so any same-named method demanded <c>overrule</c> — a
/// subclass could not add <c>f(a: string)</c> beside an inherited <c>f(a: int)</c>, nor even
/// <c>f(a: int, b: int)</c>. Fixing that alone would have been a trap: resolution gathered
/// candidates from the nearest class that declared the name, so the newly-legal declaration then
/// bound <i>every</i> call, and <c>$d.f(1)</c> reached <c>f(a: string)</c> by coercion.
/// </item>
/// </list>
/// <para>
/// A method now runs in the class that declared it rather than the one the call arrived at, which
/// is what keeps its <c>$super</c> pointing at its own base instead of skipping a level.
/// </para>
/// </remarks>
public sealed class ClassHierarchyLookupTests
{
    private static async Task<string> RunAsync(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync(source);
        return string.Join(",", results.Select(value => value?.ToString() ?? "null"));
    }

    private static async Task<string> ErrorFor(string source)
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(async () => await RunAsync(source));
        return error.Message;
    }

    // ── Statics are inherited ──────────────────────────────────────────────────

    [Fact]
    public async Task A_static_method_is_callable_through_a_subclass()
    {
        Assert.Equal("7", await RunAsync(
            """
            class B { static func s() -> int { return 7 } }
            class D extends B { }
            D.s()
            """));
    }

    [Fact]
    public async Task A_static_property_is_readable_through_a_subclass()
    {
        Assert.Equal("7", await RunAsync(
            """
            class B { static prop S = 7 }
            class D extends B { }
            D.S
            """));
    }

    [Fact]
    public async Task Statics_reach_through_more_than_one_level()
    {
        Assert.Equal("3", await RunAsync(
            """
            class A { static func s() -> int { return 3 } }
            class B extends A { }
            class C extends B { }
            C.s()
            """));
    }

    [Fact]
    public async Task A_static_on_its_own_class_is_unchanged()
    {
        // The control: this always worked, and is the behaviour the walk had to preserve.
        Assert.Equal("7", await RunAsync("class B { static func s() -> int { return 7 } }\nB.s()"));
    }

    [Fact]
    public async Task A_shy_static_stays_hidden_through_a_subclass()
    {
        // Inheriting the lookup must not inherit past the visibility rule: the base answers for
        // its own members, so `shy` is still applied by the class that declared it.
        Assert.Contains(
            "shy",
            await ErrorFor(
                """
                class B { shy static func s() -> int { return 7 } }
                class D extends B { }
                D.s()
                """),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_unknown_static_is_still_a_diagnostic()
    {
        Assert.Contains(
            "not found",
            await ErrorFor("class B { }\nclass D extends B { }\nD.nope()"),
            StringComparison.OrdinalIgnoreCase);
    }

    // ── `vital` is inherited ───────────────────────────────────────────────────

    [Fact]
    public async Task An_inherited_vital_property_must_still_be_provided()
    {
        Assert.Contains(
            "must be provided a value",
            await ErrorFor("class B { vital prop X: int }\nclass D extends B { }\nnew D()"),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_inherited_vital_property_is_satisfied_by_the_subclass()
    {
        Assert.Equal("3", await RunAsync(
            """
            class B { vital prop X: int }
            class D extends B { D(x: int) { $this.X = $x } }
            (new D(3)).X
            """));
    }

    [Theory]
    // The controls: `vital` on the class being constructed always worked, both ways of setting it.
    [InlineData("class C { vital prop X: int\n    C(x: int) { $this.X = $x } }\n(new C(3)).X")]
    [InlineData("class C(x: int) { vital prop X: int = $x }\n(new C(3)).X")]
    public async Task A_directly_declared_vital_property_is_unchanged(string source)
    {
        Assert.Equal("3", await RunAsync(source));
    }

    // ── Overload sets survive inheritance ──────────────────────────────────────

    [Fact]
    public async Task A_subclass_may_add_an_overload_of_a_different_type()
    {
        Assert.Equal("int,str", await RunAsync(
            """
            class Base { func f(a: int) -> string { return "int" } }
            class D extends Base { func f(a: string) -> string { return "str" } }
            var d = new D()
            $d.f(1)
            $d.f("x")
            """));
    }

    [Fact]
    public async Task A_subclass_may_add_an_overload_of_a_different_arity()
    {
        Assert.Equal("int,two", await RunAsync(
            """
            class Base { func f(a: int) -> string { return "int" } }
            class D extends Base { func f(a: int, b: int) -> string { return "two" } }
            var d = new D()
            $d.f(1)
            $d.f(1, 2)
            """));
    }

    [Fact]
    public async Task An_overload_declared_two_levels_up_stays_callable()
    {
        Assert.Equal("a,c", await RunAsync(
            """
            class A { func f(a: int) -> string { return "a" } }
            class B extends A { }
            class C extends B { func f(a: string) -> string { return "c" } }
            var c = new C()
            $c.f(1)
            $c.f("x")
            """));
    }

    [Fact]
    public async Task An_inherited_overload_set_is_unchanged()
    {
        // The control: a set declared entirely on the base always resolved, because the lookup
        // delegated wholesale when the subclass declared nothing of that name.
        Assert.Equal("int,str", await RunAsync(
            """
            class Base {
                func f(a: int) -> string { return "int" }
                func f(a: string) -> string { return "str" }
            }
            class D extends Base { }
            var d = new D()
            $d.f(1)
            $d.f("x")
            """));
    }

    // ── Overriding still means overriding ──────────────────────────────────────

    [Theory]
    // A matching signature is an override and still has to say so. Untyped parameters match each
    // other, so the rule is not escapable by leaving annotations off.
    [InlineData("class Base { func f(a: int) -> string { return \"b\" } }\nclass D extends Base { func f(a: int) -> string { return \"d\" } }")]
    [InlineData("class Base { func f(a) { return \"b\" } }\nclass D extends Base { func f(a) { return \"d\" } }")]
    public async Task A_matching_signature_still_requires_overrule(string source)
    {
        Assert.Contains("overrule", await ErrorFor(source), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_overrule_replaces_the_method_it_overrides()
    {
        Assert.Equal("d", await RunAsync(
            """
            class Base { func f(a: int) -> string { return "b" } }
            class D extends Base { overrule func f(a: int) -> string { return "d" } }
            (new D()).f(1)
            """));
    }

    [Fact]
    public async Task An_inherited_method_runs_with_its_own_super()
    {
        // Resolution moved across the chain, so the winner has to execute in the class that
        // declared it. Run from the subclass's definition instead, B's `$super` would be *C*'s
        // base — B itself — and the call would recurse or miss A entirely.
        Assert.Equal("A", await RunAsync(
            """
            class A { func tag() -> string { return "A" } }
            class B extends A { func viaSuper() -> string { return $super.tag() } }
            class C extends B { }
            (new C()).viaSuper()
            """));
    }

    [Fact]
    public async Task A_shy_method_on_the_base_is_not_callable_from_outside()
    {
        await Assert.ThrowsAnyAsync<Exception>(async () => await RunAsync(
            """
            class B { shy func s() -> int { return 1 } }
            class D extends B { }
            (new D()).s()
            """));
    }

    // ── Generic bindings: characterization, since the claim did not reproduce ──

    [Fact]
    public async Task The_specifications_generic_inheritance_example_works()
    {
        Assert.Equal("3", await RunAsync(
            """
            class _Point<T1, T2> {
                hollow prop X: T1
                hollow prop Y: T2
            }

            class Point2D<T1, T2> extends _Point<T1, T2> {
                overrule prop X: T1
                overrule prop Y: T2

                Point2D<T1, T2>(x: T1, y: T2) {
                    $this.X = $x
                    $this.Y = $y
                }
            }

            var p = new Point2D<int, int>(3, 4)
            $p.X
            """));
    }

    [Theory]
    // A type parameter passed through an intermediary, fixed to a concrete type, and constrained.
    [InlineData(
        "class A<T>(v: T) { prop value: T = $v\n    func unwrap() -> T { return $this.value } }\n" +
        "class B<T>(v: T) extends A<T>($v) { }\nclass C(v: int) extends B<int>($v) { }\n(new C(5)).unwrap()",
        "5")]
    [InlineData(
        "class N<T>(v: T) where T: Numeric { prop value: T = $v\n    func unwrap() -> T { return $this.value } }\n" +
        "class D<T>(v: T) extends N<T>($v) where T: Numeric { }\n(new D<int>(4)).unwrap()",
        "4")]
    public async Task Type_arguments_reach_through_the_chain(string source, string expected)
    {
        Assert.Equal(expected, await RunAsync(source));
    }

    [Fact]
    public async Task An_inherited_generic_property_keeps_its_type()
    {
        Assert.Equal("String", await RunAsync(
            """
            class Box<T>(initial: T) { prop value: T = $initial }
            class M<T>(v: T) extends Box<T>($v) { }
            var b = new M<string>("hi")
            ($b.value | type-of).Name
            """));
    }
}
