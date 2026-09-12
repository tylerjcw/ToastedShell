using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// A trait's default body resolves the types it names where the trait was
/// <em>written</em> — <c>TOAST-0132</c>.
/// </summary>
/// <remarks>
/// <para>
/// <c>TOAST-0122</c> made a library's <em>annotations</em> resolve at their declaration.
/// A trait's <em>body</em> still did not, and that is a different mechanism: trait
/// defaults are copied into every adopting class as ordinary methods, and the copy
/// captured the scopes of the *adopting* site. A body that names a type — and
/// <c>Polygonal.Bounds()</c> builds its answer with
/// <c>new ToastLib.Math.Geometry.Rectangle(…)</c> — then looked that name up in the
/// user's file, where an aliased import has not put it.
/// </para>
/// <para>
/// So a trait was usable from a library only if its default bodies never named a type,
/// which is the opposite of what a trait is for: <c>Polygonal</c> exists precisely to
/// derive <c>Bounds</c> and <c>Area</c> for you.
/// </para>
/// <para>
/// The trait now records the scopes visible where it was declared, and the injected
/// default uses those instead — a body belongs to the trait, and resolves where the
/// trait was written, as a closure does.
/// </para>
/// </remarks>
public sealed class TraitBodyScopeTests : IDisposable
{
    private readonly string _root =
        Directory.CreateTempSubdirectory("tosh-trait-body-scope-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Write(string name, string source)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, source);
        return path;
    }

    private static async Task<IReadOnlyList<object?>> RunAsync(string script)
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime.Language);
        return await engine.ExecuteToListAsync(script);
    }

    /// <summary>A library whose trait default names one of the library's own types.</summary>
    private const string Library = """
        module Lib {
            export class Boxed(v: double) { prop V: double = $v }

            export trait Derives {
                func Raw() -> double

                func Wrapped() {
                    return new Lib.Boxed($this.Raw())
                }
            }
        }
        """;

    [Fact]
    public async Task A_trait_default_resolves_its_own_types_after_an_aliased_import()
    {
        var lib = Write("lib.tosh", Library);

        var results = await RunAsync(
            $"require Lib from \"{lib}\" as L\n"
            + "class Mine() uses L.Derives { func Raw() -> double => 7.0 }\n"
            + "((new Mine()).Wrapped()).V");

        Assert.Equal(7.0, Assert.Single(results));
    }

    /// <summary>
    /// The half that came free and must stay: a method the class declares itself still
    /// resolves in the class's own file, so adopting a trait does not move the rest of the
    /// class somewhere else.
    /// </summary>
    [Fact]
    public async Task An_adopting_class_still_resolves_its_own_names()
    {
        var lib = Write("lib.tosh", Library);

        var results = await RunAsync(
            $"require Lib from \"{lib}\" as L\n"
            + "class Local(v) { prop V = $v }\n"
            + "class Mine() uses L.Derives {\n"
            + "    func Raw() -> double => 7.0\n"
            + "    func Own() => (new Local(3)).V\n"
            + "}\n"
            + "(new Mine()).Own()");

        Assert.Equal(3, Assert.Single(results));
    }

    /// <summary>
    /// And a class that overrides the default keeps its own body — the injection only
    /// supplies what the class did not define, which the adopting scope must still serve.
    /// </summary>
    [Fact]
    public async Task A_class_that_defines_the_member_itself_wins()
    {
        var lib = Write("lib.tosh", Library);

        var results = await RunAsync(
            $"require Lib from \"{lib}\" as L\n"
            + "class Own(v) { prop V = $v }\n"
            + "class Mine() uses L.Derives {\n"
            + "    func Raw() -> double => 7.0\n"
            + "    func Wrapped() => (new Own(99))\n"
            + "}\n"
            + "((new Mine()).Wrapped()).V");

        Assert.Equal(99, Assert.Single(results));
    }
}
