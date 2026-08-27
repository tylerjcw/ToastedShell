using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// A type annotation written inside a class body belongs to the module that body
/// lives in, not to wherever the member is eventually called from.
///
/// Annotations are checked when a member <em>runs</em>, and by then the module
/// scope that declared the class is long gone from the engine's stack — so a
/// class inside a module could not name itself, or a sibling, without writing
/// the module path out in full. `TS-P2-106`.
/// </summary>
public class ModuleScopedAnnotationTests
{
    private static async Task<string> RunAsync(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);
        var results = await engine.ExecuteToListAsync(source);
        return string.Join(",", results.Select(value => value?.ToString() ?? "null"));
    }

    /// <summary>
    /// The reported shape: a static factory whose return annotation names the
    /// class that declares it.
    /// </summary>
    [Fact]
    public async Task A_class_in_a_module_names_itself_in_a_return_annotation()
    {
        var output = await RunAsync(
            """
            export partial module One {
                export class A {
                    prop N: int = 0
                    static func Make(n: int) -> A {
                        var made = new A()
                        $made.N = $n
                        return $made
                    }
                }
            }
            (One.A.Make(3)).N
            """);

        Assert.Equal("3", output);
    }

    /// <summary>
    /// Nested modules were the case that surfaced this, via `ToastLib.Gl.Mesh`.
    /// </summary>
    [Fact]
    public async Task A_class_in_a_nested_module_names_itself()
    {
        var output = await RunAsync(
            """
            export partial module Outer {
                export partial module Inner {
                    export class Mesh {
                        prop N: int = 7
                        static func Make() -> Mesh => new Mesh()
                    }
                }
            }
            (Outer.Inner.Mesh.Make()).N
            """);

        Assert.Equal("7", output);
    }

    /// <summary>
    /// Siblings resolve the same way, in both declaration orders — the fix must
    /// not depend on the sibling having been declared first.
    /// </summary>
    [Fact]
    public async Task A_class_in_a_module_names_a_sibling_in_either_order()
    {
        var output = await RunAsync(
            """
            export partial module M {
                export class Earlier { prop N: int = 1 }
                export class Middle {
                    static func Back() -> Earlier => new Earlier()
                    static func Forward() -> Later => new Later()
                }
                export class Later { prop N: int = 2 }
            }
            (M.Middle.Back()).N
            (M.Middle.Forward()).N
            """);

        Assert.Equal("1,2", output);
    }

    /// <summary>
    /// A property annotation takes the same path as a return annotation, and was
    /// equally affected.
    /// </summary>
    [Fact]
    public async Task A_property_annotation_resolves_against_the_declaring_module()
    {
        var output = await RunAsync(
            """
            export partial module P {
                export class Node { prop Label: string = "n" }
                export class Holder {
                    prop Child: Node = new Node()
                }
            }
            (new P.Holder()).Child.Label
            """);

        Assert.Equal("n", output);
    }

    /// <summary>
    /// A refinement type declared in a module resolves by its bare name from a
    /// class in that module — `TS-P2-98`, the same root cause as the class case
    /// but reached through a separate registry.
    /// </summary>
    [Fact]
    public async Task A_refinement_type_resolves_unqualified_inside_its_module()
    {
        var output = await RunAsync(
            """
            export partial module R {
                export type Unit = double where (_ >= 0.0 and _ <= 1.0) coerce Math.Clamp(_, 0.0, 1.0)
                export class Chan { prop V: Unit = 0.25 }
            }
            (new R.Chan()).V
            """);

        Assert.Equal("0.25", output);
    }

    /// <summary>
    /// Resolving the name is not enough — the refinement still has to run. A
    /// lookup that found the type but dropped its predicate would pass the test
    /// above and be useless.
    /// </summary>
    [Fact]
    public async Task A_module_local_refinement_still_coerces_on_assignment()
    {
        var output = await RunAsync(
            """
            export partial module R {
                export type Unit = double where (_ >= 0.0 and _ <= 1.0) coerce Math.Clamp(_, 0.0, 1.0)
                export class Chan { prop V: Unit = 0.0 }
            }
            var c = new R.Chan()
            $c.V = 5.0
            $c.V
            $c.V = -3.0
            $c.V
            """);

        Assert.Equal("1,0", output);
    }

    /// <summary>
    /// The declaring module must not leak: a genuinely unknown annotation still
    /// has to be reported, and a name that only exists in some *other* module
    /// must not start resolving.
    /// </summary>
    [Fact]
    public async Task An_unknown_annotation_is_still_rejected()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);

        var exception = await Assert.ThrowsAsync<ToshDiagnosticException>(
            () => engine.ExecuteToListAsync(
                """
                export partial module Q {
                    export class Only {
                        static func Make() -> NoSuchType => new Only()
                    }
                }
                Q.Only.Make()
                """));

        Assert.Contains("NoSuchType", exception.Message);
    }
}
