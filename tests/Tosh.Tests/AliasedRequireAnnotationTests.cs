using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// A library's own type annotations resolve where they were written, not where the
/// call happens — <c>TOAST-0122</c>.
/// </summary>
/// <remarks>
/// <para>
/// <c>require X from "…" as Alias</c> binds the alias and nothing else, which is a
/// defensible selective import. What was not defensible is what it did to the imported
/// code: a method annotated <c>amount: ToastLib.Math.Vector2D&lt;T&gt;</c> had that
/// annotation resolved in the *caller's* scope, which under an alias has no
/// <c>ToastLib</c> — so the parameter type resolved to nothing, every argument scored
/// as a mismatch, and it surfaced as "no overload matched with 1 argument(s)": a
/// message about arity for a problem about names.
/// </para>
/// <para>
/// The return annotation already resolved in the declaring module. Parameters did not,
/// and overload *scoring* is where a parameter annotation is read. Two halves were
/// needed: entering the declaring scope around scoring, and teaching the export table
/// its own dotted path so a self-qualified name can be recognised there — the table is
/// keyed on bare names, so <c>ToastLib.Math.Vector2D</c> missed it entirely.
/// </para>
/// <para>
/// Only the module's *own* prefix is stripped. Matching on the last segment instead
/// would resolve <c>Other.Vec</c> to this module's <c>Vec</c>, which is a wrong answer
/// where an error is the right one — the third test holds that line.
/// </para>
/// </remarks>
public sealed class AliasedRequireAnnotationTests : IDisposable
{
    private readonly string _root =
        Directory.CreateTempSubdirectory("tosh-aliased-require-").FullName;

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

    /// <summary>A library that annotates its own types by their full path.</summary>
    private const string SelfQualified = """
        module Mini {
            export class Vec(x: double) { prop X: double = $x }
            export class Pt(x: double) {
                prop X: double = $x
                func Move(amount: Mini.Vec) -> Mini.Pt => (new Mini.Pt(($this.X + $amount.X)))
            }
        }
        """;

    [Fact]
    public async Task An_aliased_import_can_call_a_method_annotated_with_its_own_types()
    {
        var lib = Write("mini.tosh", SelfQualified);

        var results = await RunAsync(
            $"require Mini from \"{lib}\" as M\n"
            + "var p = (new M.Pt(1.0))\n"
            + "($p.Move((new M.Vec(2.0)))).X");

        Assert.Equal(3.0, Assert.Single(results));
    }

    /// <summary>
    /// The acceptance clause that guards against the cheap fix. Making the canonical
    /// path reachable would have fixed the case above by making a selective import
    /// non-selective; the caller still must not see <c>Mini</c>.
    /// </summary>
    [Fact]
    public async Task A_selective_import_still_binds_only_the_alias()
    {
        var lib = Write("mini.tosh", SelfQualified);

        await Assert.ThrowsAnyAsync<Exception>(() => RunAsync(
            $"require Mini from \"{lib}\" as M\n(new Mini.Pt(1.0)).X"));
    }

    /// <summary>
    /// A prefix that is *not* the declaring module's own must still fail. Resolving it
    /// by last segment would find this module's `Vec` and quietly answer the wrong
    /// question.
    /// </summary>
    [Fact]
    public async Task A_different_modules_prefix_is_not_resolved_here()
    {
        var lib = Write("other.tosh", """
            module Other { export class Vec(x: double) { prop X: double = $x } }
            module Mini {
                export class Vec(x: double) { prop X: double = $x }
                export class Pt(x: double) {
                    prop X: double = $x
                    func Move(amount: Other.Vec) -> Mini.Pt => (new Mini.Pt(($this.X + $amount.X)))
                }
            }
            """);

        await Assert.ThrowsAnyAsync<Exception>(() => RunAsync(
            $"require Mini from \"{lib}\" as M\n"
            + "var p = (new M.Pt(1.0))\n"
            + "($p.Move((new M.Vec(2.0)))).X"));
    }

    /// <summary>The plain import, which always worked and must keep working.</summary>
    [Fact]
    public async Task A_plain_import_is_unaffected()
    {
        var lib = Write("mini.tosh", SelfQualified);

        var results = await RunAsync(
            $"require \"{lib}\"\n"
            + "var p = (new Mini.Pt(1.0))\n"
            + "($p.Move((new Mini.Vec(2.0)))).X");

        Assert.Equal(3.0, Assert.Single(results));
    }

    /// <summary>
    /// The diagnostic the row asked for regardless of which route the rest took: an
    /// unresolvable *parameter type* is named, rather than reported as an arity
    /// mismatch when the arity is right.
    /// </summary>
    [Fact]
    public async Task An_unresolvable_parameter_type_is_named()
    {
        var lib = Write("other.tosh", """
            module Other { export class Vec(x: double) { prop X: double = $x } }
            module Mini {
                export class Pt(x: double) {
                    prop X: double = $x
                    func Move(amount: Other.Vec) -> Mini.Pt => (new Mini.Pt($this.X))
                }
            }
            """);

        var error = await Assert.ThrowsAnyAsync<Exception>(() => RunAsync(
            $"require Mini from \"{lib}\" as M\n(new M.Pt(1.0)).Move(1.0)"));

        Assert.Contains("amount", error.Message, StringComparison.Ordinal);
        Assert.Contains("Other.Vec", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("argument(s)", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// And the control for that: a parameter annotated with a type parameter resolves
    /// through the same predicate and must not be reported as unresolvable.
    /// </summary>
    [Fact]
    public async Task A_type_parameter_annotation_is_not_mistaken_for_an_unknown_type()
    {
        var results = await RunAsync(
            "class Box<T>(v: T) { func Same(other: T) => $other }\n"
            + "(new Box<int>(1)).Same(5)");

        Assert.Equal(5, Assert.Single(results));
    }
}
