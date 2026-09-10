using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// An interface or trait names a contract, and an annotation naming one accepts
/// any class that satisfies it.
///
/// Before `TS-P2-99` neither could be used as an annotation at all: the type was
/// known and `is` answered correctly, but a parameter annotated with it rejected
/// every value, so a polymorphic signature had to go unannotated and duck-type —
/// losing exactly the documentation the annotation was for. `func visit(node:
/// AstNode)` is the shape of every compiler pass, which is where this surfaced.
/// </summary>
public class ContractAnnotationTests
{
    private static async Task<string> RunAsync(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);
        var results = await engine.ExecuteToListAsync(source);
        return string.Join(",", results.Select(value => value?.ToString() ?? "null"));
    }

    [Fact]
    public async Task An_interface_annotation_accepts_a_class_that_fulfills_it()
    {
        var output = await RunAsync(
            """
            interface Drawable { func Draw() -> string }
            class Circle fulfills Drawable { func Draw() -> string => "circle" }
            func render(d: Drawable) -> string => $d.Draw()
            render(new Circle())
            """);

        Assert.Equal("circle", output);
    }

    // ── trait bodies parse like class bodies ──────────────────────────────
    //
    // A trait body is a brace-delimited member list separated by newlines, but it
    // never registered itself as a boundary owner, so the element-boundary check
    // fell through to a heuristic that does not fire there. Two consequences, both
    // fixed together: an arrow-bodied default consumed whatever member followed it,
    // and `##` documentation was reported as an unexpected member.

    [Fact]
    public async Task An_arrow_bodied_trait_default_does_not_swallow_the_next_member()
    {
        var output = await RunAsync(
            """
            trait Sized {
                func Width() -> int => 3
                func Height() -> int => 4
                func Area() -> int { return ($this.Width() * $this.Height()) }
            }
            class Box uses Sized { prop Name = "box" }
            echo (new Box()).Area()
            """);

        Assert.Equal("12", output);
    }

    [Fact]
    public async Task A_trait_member_may_be_documented()
    {
        var output = await RunAsync(
            """
            trait Named {
                ## The name this value answers to.
                prop Name: string

                ## A greeting built from the name.
                func Greet() -> string => $"hello {$this.Name}"
            }
            class Person uses Named { prop Name: string = "Ada" }
            echo (new Person()).Greet()
            """);

        Assert.Equal("hello Ada", output);
    }

    [Fact]
    public async Task A_trait_still_rejects_a_member_that_is_neither_func_nor_prop()
    {
        // The doc-comment fix must not turn the trait body into a block that accepts
        // anything: a stray statement is still an error.
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);

        await Assert.ThrowsAnyAsync<Exception>(
            () => engine.ExecuteToListAsync(
                """
                trait Bad { var x = 1 }
                class C uses Bad { prop N = 1 }
                """));
    }

    [Theory]
    [InlineData("uses Requires, Provides")]
    [InlineData("uses Provides, Requires")]
    public async Task One_trait_may_satisfy_another_whatever_order_they_are_listed_in(string usesClause)
    {
        // Traits were applied in one pass, so a requirement was checked before a
        // later trait's default could satisfy it: listing the requirer first failed
        // and listing the provider first worked. Which order a class names its
        // traits in is not part of whether it satisfies them.
        var output = await RunAsync(
            $$"""
            trait Requires { func Area() -> int }
            trait Provides { func Area() -> int { return 12 } }
            class Tile {{usesClause}} { prop Name = "tile" }
            echo (new Tile()).Area()
            """);

        Assert.Equal("12", output);
    }

    [Fact]
    public async Task A_member_no_trait_supplies_is_still_reported_missing()
    {
        // The injection pass must not make the check vacuous.
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);

        var error = await Assert.ThrowsAsync<ToshDiagnosticException>(
            () => engine.ExecuteToListAsync(
                """
                trait Requires { func Area() -> int }
                trait Unrelated { func Name() -> string { return "x" } }
                class Tile uses Requires, Unrelated { prop N = 1 }
                """));

        Assert.Contains(error.Diagnostics, d => d.Code == "tosh.runtime.missing_trait_methods");
    }

    [Fact]
    public async Task A_trait_annotation_accepts_a_class_that_uses_it()
    {
        var output = await RunAsync(
            """
            trait Named { prop Name = "anon" }
            class Square uses Named { }
            func label(n: Named) -> string => $n.Name
            label(new Square())
            """);

        Assert.Equal("anon", output);
    }

    /// <summary>
    /// The contract may be satisfied by a base class rather than declared on the
    /// value's own class.
    /// </summary>
    [Fact]
    public async Task A_contract_inherited_from_a_base_class_counts()
    {
        var output = await RunAsync(
            """
            interface Drawable { func Draw() -> string }
            class Shape fulfills Drawable { func Draw() -> string => "shape" }
            class Wedge extends Shape { }
            func render(d: Drawable) -> string => $d.Draw()
            render(new Wedge())
            """);

        Assert.Equal("shape", output);
    }

    /// <summary>
    /// Variables and returns take the same path as parameters.
    /// </summary>
    [Fact]
    public async Task An_interface_works_as_a_variable_and_return_annotation()
    {
        var output = await RunAsync(
            """
            interface Drawable { func Draw() -> string }
            class Circle fulfills Drawable { func Draw() -> string => "circle" }
            func make() -> Drawable => new Circle()
            var d: Drawable = make()
            $d.Draw()
            """);

        Assert.Equal("circle", output);
    }

    /// <summary>
    /// The negative control, and the one that matters: a class with a
    /// structurally identical member but no declared contract is still rejected.
    /// Accepting it would turn the annotation into duck typing with extra steps.
    /// </summary>
    [Fact]
    public async Task A_class_that_does_not_declare_the_contract_is_rejected()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);

        var exception = await Assert.ThrowsAsync<ToshDiagnosticException>(
            () => engine.ExecuteToListAsync(
                """
                interface Drawable { func Draw() -> string }
                class Loose { func Draw() -> string => "loose" }
                func render(d: Drawable) -> string => $d.Draw()
                render(new Loose())
                """));

        Assert.Contains("Drawable", exception.Message);
    }
}
