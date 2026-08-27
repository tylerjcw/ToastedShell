using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// Types declared inside a class body.
/// </summary>
/// <remarks>
/// <para>
/// Reported as "enums cannot be used inside classes". Probing widened it: <i>nothing</i> nested.
/// <c>class</c>, <c>struct</c>, <c>record</c>, <c>union</c>, <c>interface</c> and <c>trait</c> all
/// failed identically with <c>tosh.parser.expected_class_member</c>, because the class member
/// parser accepted members and nothing else. So this is not an enum feature; it is the missing
/// general one, and all seven declaration keywords are accepted rather than the one that was
/// asked about — a class that can nest some kinds of type and not others is a rule nobody can
/// remember.
/// </para>
/// <para>
/// A nested type is a static member of the class that declares it. <c>Outer.Inner</c> resolves
/// through the same static lookup as any other static member, and <c>Outer.Inner.Member</c>
/// follows by ordinary member access on the type it returns. The declaration itself is parsed and
/// evaluated by exactly the code that handles a top-level one, so a nested enum is the same enum
/// and every rule governing an outer declaration governs this one for free.
/// </para>
/// <para>
/// Naming a nested type needed its own step. Reading one through member access works the moment
/// it is a static member, which made the enum case look finished; but <c>new Outer.Inner()</c> and
/// an <c>Outer.Inner</c> annotation both resolve a *type name* rather than a value, and that path
/// knew nothing of nesting.
/// </para>
/// </remarks>
public sealed class NestedTypeTests
{
    private static async Task<string> RunAsync(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);
        var results = await engine.ExecuteToListAsync(source);
        return string.Join(",", results.Select(value => value?.ToString() ?? "null"));
    }

    // ── The reported case ──────────────────────────────────────────────────────

    [Fact]
    public async Task An_enum_can_be_declared_inside_a_class()
    {
        Assert.Equal("Uranium", await RunAsync(
            """
            class Reactor {
                enum Fuel : int {
                    Mox = 3
                    Uranium = 8
                }
                prop Name = "r"
            }
            Reactor.Fuel.Uranium
            """));
    }

    [Fact]
    public async Task A_nested_name_does_not_leak_into_the_surrounding_scope()
    {
        // The point of nesting: `Fuel` belongs to `Reactor`, and declaring one does not put the
        // other name beside it. The declaration is evaluated in a scope of its own for exactly
        // this reason.
        await Assert.ThrowsAnyAsync<Exception>(async () => await RunAsync(
            """
            class Reactor {
                enum Fuel : int { Mox = 3 }
            }
            var x: Fuel = Fuel.Mox
            """));
    }

    // ── Every declaration keyword nests ────────────────────────────────────────

    [Theory]
    [InlineData("enum Inner : int { A = 1 }")]
    [InlineData("class Inner { prop V = 7 }")]
    [InlineData("struct Inner { prop V = 7 }")]
    [InlineData("record Inner(a, b)")]
    [InlineData("interface Inner { }")]
    [InlineData("trait Inner { }")]
    [InlineData("union Inner { }")]
    public async Task Every_type_keyword_may_be_nested(string declaration)
    {
        Assert.Equal("ok", await RunAsync($"class Outer {{ {declaration} }}\n\"ok\""));
    }

    // ── A nested type can be named, not merely read ────────────────────────────

    [Fact]
    public async Task A_nested_class_can_be_constructed_by_its_qualified_name()
    {
        Assert.Equal("7", await RunAsync(
            """
            class Outer { class Inner { prop V = 7 } }
            (new Outer.Inner()).V
            """));
    }

    [Fact]
    public async Task A_qualified_nested_name_works_as_a_type_annotation()
    {
        Assert.Equal("7", await RunAsync(
            """
            class Outer { class Inner { prop V = 7 } }
            var i: Outer.Inner = new Outer.Inner()
            $i.V
            """));
    }

    [Fact]
    public async Task Nesting_reaches_more_than_one_level_deep()
    {
        // Resolution recurses through the ordinary type lookup, so depth costs nothing extra.
        Assert.Equal("5", await RunAsync(
            """
            class A { class B { class C { prop V = 5 } } }
            (new A.B.C()).V
            """));
    }

    [Fact]
    public async Task An_unknown_nested_name_is_still_a_diagnostic()
    {
        await Assert.ThrowsAnyAsync<Exception>(async () => await RunAsync(
            "class Outer { class Inner { } }\nnew Outer.Nope()"));
    }

    // ── Visibility ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_shy_nested_type_is_not_reachable_from_outside()
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(async () => await RunAsync(
            """
            class Reactor {
                shy enum Fuel : int { Mox = 3 }
            }
            Reactor.Fuel.Mox
            """));

        Assert.Contains("shy", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── Nothing that already worked changed ────────────────────────────────────

    [Theory]
    [InlineData("enum Fuel : int { Mox = 3 }\nFuel.Mox", "Mox")]
    [InlineData("class C { prop X = 4 }\n(new C()).X", "4")]
    [InlineData("class C { prop X = 1\n    func f() { return 2 } }\n(new C()).f()", "2")]
    public async Task Top_level_declarations_are_unaffected(string source, string expected)
    {
        Assert.Equal(expected, await RunAsync(source));
    }

    [Fact]
    public async Task A_class_with_no_nested_types_is_unaffected()
    {
        Assert.Equal("3", await RunAsync(
            """
            class Point {
                prop X = 1
                prop Y = 2
                func sum() { return ($this.X + $this.Y) }
            }
            (new Point()).sum()
            """));
    }

    // ── Inside the class, the qualification is unnecessary ─────────────────────

    [Fact]
    public async Task A_property_initialiser_names_a_nested_type_directly()
    {
        // The class is already inside itself, so writing `Reactor.Fuel.Mox` there would be noise.
        // Before this, `Fuel.Mox` in an initialiser read as a bareword and produced the literal
        // text "Fuel.Mox".
        Assert.Equal("Mox", await RunAsync(
            """
            class Reactor {
                enum Fuel : int {
                    Mox = 3
                    Uranium = 8
                }
                prop Loaded = Fuel.Mox
            }
            (new Reactor()).Loaded
            """));
    }

    [Fact]
    public async Task A_nested_type_may_annotate_a_member_of_its_own_class()
    {
        Assert.Equal("Mox", await RunAsync(
            """
            class Reactor {
                enum Fuel : int { Mox = 3 }
                prop Loaded: Fuel = Fuel.Mox
            }
            (new Reactor()).Loaded
            """));
    }

    [Fact]
    public async Task A_method_body_names_a_nested_type_directly()
    {
        Assert.Equal("Uranium", await RunAsync(
            """
            class Reactor {
                enum Fuel : int { Uranium = 8 }
                func pick() { return Fuel.Uranium }
            }
            (new Reactor()).pick()
            """));
    }

    [Fact]
    public async Task A_nested_class_is_constructible_by_its_bare_name_from_within()
    {
        Assert.Equal("7", await RunAsync(
            """
            class Outer {
                class Inner { prop V = 7 }
                func make() { return new Inner() }
            }
            (new Outer()).make().V
            """));
    }

    [Fact]
    public async Task The_bare_name_is_confined_to_the_declaring_class()
    {
        // The scope carrying the nested names is pushed for the class's own code and popped with
        // it, so nothing outside gains the unqualified spelling.
        await Assert.ThrowsAnyAsync<Exception>(async () => await RunAsync(
            """
            class Reactor { enum Fuel : int { Mox = 3 } }
            var x: Fuel = 1
            """));
    }

    [Fact]
    public async Task An_inherited_nested_type_is_named_directly_by_a_subclass()
    {
        // Nested types follow members: a subclass sees what its base declares.
        Assert.Equal("Mox", await RunAsync(
            """
            class Base { enum Fuel : int { Mox = 3 } }
            class Derived extends Base {
                func pick() { return Fuel.Mox }
            }
            (new Derived()).pick()
            """));
    }
}
