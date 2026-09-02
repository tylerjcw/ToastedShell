using Tosh.Language;
using Tosh.Language.Parsing;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// The <c>::</c> path operator — <c>TOAST-0090</c>.
/// </summary>
/// <remarks>
/// <para>
/// <c>::</c> reaches into a <i>type</i>; <c>.</c> reaches into a <i>value</i>. Both spellings
/// resolve to the same thing, so a path is canonicalised to dots at parse time and every consumer
/// below sees one form; only the syntax node remembers which operator was written.
/// </para>
/// <para>
/// The lexer needed no change — <c>::</c> already stayed inside a bareword, which is why
/// <c>System::Math::PI</c> arrived at the engine as an unknown <i>command</i> rather than a parse
/// error. Recognition was the missing part, and it is spread across more places than the reading
/// of a path suggests: a constructor target, a static call, an assignment target and a type
/// annotation each reach the name by a different route, and each had to learn the operator
/// separately. Every one of them is covered below, because the first four found here were found
/// by probing rather than by reading.
/// </para>
/// </remarks>
public sealed class PathOperatorTests
{
    private static async Task<string> RunAsync(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);
        var results = await engine.ExecuteToListAsync(source);
        return string.Join(",", results.Select(value => value?.ToString() ?? "null"));
    }

    // ── Reaching into a type ───────────────────────────────────────────────────

    [Theory]
    [InlineData("System::Math::PI", "3.141592653589793")]
    [InlineData("System::Math::Max(3, 9)", "9")]
    public async Task A_static_member_is_reached_by_the_path_operator(string source, string expected)
    {
        Assert.Equal(expected, await RunAsync(source));
    }

    [Fact]
    public async Task An_enum_member_is_reached_by_the_path_operator()
    {
        Assert.Equal("Uranium", await RunAsync(
            """
            enum Fuel : int { Mox = 3, Uranium = 8 }
            Fuel::Uranium
            """));
    }

    [Fact]
    public async Task A_nested_type_is_reached_by_the_path_operator()
    {
        Assert.Equal("Mox", await RunAsync(
            """
            class Reactor { enum Fuel : int { Mox = 3 } }
            Reactor::Fuel::Mox
            """));
    }

    [Fact]
    public async Task A_union_variant_is_constructed_by_the_path_operator()
    {
        Assert.Equal("42", await RunAsync(
            """
            union Result { Ok(v), Err(m) }
            var r = Result::Ok(42)
            $r.v
            """));
    }

    // ── The four routes that reach a type name separately ──────────────────────

    [Fact]
    public async Task A_constructor_target_takes_the_path_operator()
    {
        Assert.Equal("7", await RunAsync(
            """
            class Outer { class Inner { prop V = 7 } }
            (new Outer::Inner()).V
            """));
    }

    [Fact]
    public async Task A_type_annotation_takes_the_path_operator()
    {
        Assert.Equal("7", await RunAsync(
            """
            class Outer { class Inner { prop V = 7 } }
            var i: Outer::Inner = new Outer::Inner()
            $i.V
            """));
    }

    [Fact]
    public async Task An_assignment_target_takes_the_path_operator()
    {
        // Before this the target was not recognised as a member path at all — the raw `B::S` has
        // no `.` to split on, so the whole text became the root name and failed the identifier
        // check, and the statement fell through to the predicate form reporting
        // `assignment_in_predicate` at the `=`.
        Assert.Equal("5", await RunAsync(
            """
            class B { static prop S = 1 }
            B::S = 5
            B::S
            """));
    }

    [Fact]
    public async Task A_cast_target_takes_the_path_operator()
    {
        Assert.Equal("8", await RunAsync(
            """
            enum Fuel : int { Uranium = 8 }
            var v = Fuel::Uranium
            cast int $v
            """));
    }

    // ── The two operators mean different things in one expression ──────────────

    [Fact]
    public async Task A_path_reaches_the_type_and_a_dot_reaches_the_value_it_returns()
    {
        // `::` into `System.DateTime`, then `.` onto the value `Now` produced. This is the
        // distinction the operator exists to draw, in one expression.
        var year = await RunAsync("System::DateTime::Now.Year");
        Assert.Equal(DateTime.Now.Year.ToString(), year);
    }

    // ── The heuristic the operator retires ─────────────────────────────────────

    [Fact]
    public async Task A_lowercase_head_needs_no_casing_heuristic()
    {
        // `.` decides between a static path and a command invocation by capitalisation, which is
        // why `Geo.area 2` needed the `TS-P2-16` carve-out. `::` says which is meant, so a
        // lowercase module resolves without the parser guessing.
        Assert.Equal("2", await RunAsync(
            """
            module geo { func area(r) { return 2 } }
            geo::area(1)
            """));
    }

    // ── The AST tells them apart ───────────────────────────────────────────────

    [Theory]
    [InlineData("Fuel::Mox", true)]
    [InlineData("Fuel.Mox", false)]
    public void The_syntax_node_records_which_operator_was_written(string path, bool expected)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);
        var parse = engine.Parse($"enum Fuel : int {{ Mox = 3 }}\n{path}", "<path-op>");

        var node = FindStaticAccess(parse.Statement);

        Assert.NotNull(node);
        Assert.Equal(expected, node!.UsedPathOperator);

        // Canonicalised either way, so everything downstream resolves one spelling.
        Assert.Equal("Fuel.Mox", node.Path);
    }

    private static StaticMemberAccessArgumentSyntax? FindStaticAccess(object? node) => node switch
    {
        StaticMemberAccessArgumentSyntax found => found,
        System.Collections.IEnumerable sequence and not string =>
            sequence.Cast<object?>().Select(FindStaticAccess).FirstOrDefault(found => found is not null),
        not null =>
            node.GetType()
                .GetProperties()
                .Where(property => property.GetIndexParameters().Length == 0 &&
                                   property.PropertyType != typeof(string) &&
                                   !property.PropertyType.IsPrimitive)
                .Select(property =>
                {
                    try { return FindStaticAccess(property.GetValue(node)); }
                    catch { return null; }
                })
                .FirstOrDefault(found => found is not null),
        _ => null,
    };

    // ── Controls ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("System.Math.PI", "3.141592653589793")]
    [InlineData("enum Fuel : int { Mox = 3 }\nFuel.Mox", "Mox")]
    [InlineData("class Outer { class Inner { prop V = 7 } }\n(new Outer.Inner()).V", "7")]
    public async Task The_dotted_spelling_is_unchanged(string source, string expected)
    {
        Assert.Equal(expected, await RunAsync(source));
    }

    [Theory]
    [InlineData("::Foo")]
    [InlineData("Foo::")]
    [InlineData("Foo:::Bar")]
    public async Task A_malformed_path_is_not_treated_as_one(string source)
    {
        // Declined rather than accepted-then-unresolvable: there is no type to reach into on the
        // left of a leading `::`, and nothing named on the right of a trailing one. Each falls
        // through to whatever the text would otherwise have meant, which here is a command.
        await Assert.ThrowsAnyAsync<Exception>(async () => await RunAsync(source));
    }

    [Fact]
    public async Task A_value_member_still_needs_a_dot()
    {
        // `::` reaches into types only. A value's member is not one, so this must not resolve.
        await Assert.ThrowsAnyAsync<Exception>(async () => await RunAsync(
            """
            class Point { prop X = 4 }
            var p = new Point()
            $p::X
            """));
    }
}
