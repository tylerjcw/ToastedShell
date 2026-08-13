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
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
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
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

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
