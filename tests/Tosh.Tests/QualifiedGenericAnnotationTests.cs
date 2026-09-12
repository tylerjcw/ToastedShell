using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// A module-qualified generic annotation matches the value it names.
/// </summary>
/// <remarks>
/// <para>
/// It did not, and that made every generic type declared in a module unusable in an
/// annotation — which is every generic type in a library, since a library is a
/// module. <c>var p: ToastLib.Math.Point2D&lt;double&gt; =
/// (ToastLib.Math.Point2D.Empty&lt;double&gt;())</c> rejected the value that
/// expression had just produced, while the identical unqualified
/// <c>Point2D&lt;double&gt;</c> accepted it.
/// </para>
/// <para>
/// The generic branch of the annotation converter matched *textually*:
/// <c>IsInstanceOf</c> splits the name at its <c>&lt;</c> and compares the open part
/// against the class and its ancestors, and a definition knows itself only by its
/// bare name — so <c>M.Box</c> matched nothing. The non-generic path never had this,
/// because it resolves the annotation to a definition and compares that; the fix
/// re-spells the name from the definition the resolver already returned.
/// </para>
/// <para>
/// Found by running the repository's examples rather than parsing them:
/// <c>examples/particle.tosh</c> annotates a property with
/// <c>ToastLib.Math.Point2D&lt;double&gt;</c> and could not get past its own first
/// field.
/// </para>
/// </remarks>
public sealed class QualifiedGenericAnnotationTests
{
    private const string Module =
        "module M { export class Box<T>(v: T) { prop V: T = $v } }\n";

    private static async Task<IReadOnlyList<object?>> RunAsync(string script)
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime.Language);
        return await engine.ExecuteToListAsync(script);
    }

    [Theory]
    // The case that was broken, in each position an annotation can appear.
    [InlineData("var b: M.Box<int> = (new M.Box<int>(1))\n$b.V")]
    [InlineData("func f(b: M.Box<int>) => $b.V\nf (new M.Box<int>(1))")]
    [InlineData("func f() -> M.Box<int> => (new M.Box<int>(1))\n(f).V")]
    // And the unqualified spelling, which always worked and must keep working.
    [InlineData("var b: Box<int> = (new M.Box<int>(1))\n$b.V")]
    public async Task A_qualified_generic_annotation_accepts_its_own_type(string script)
    {
        Assert.Equal(1, Assert.Single(await RunAsync(Module + script)));
    }

    /// <summary>
    /// The half the fix must not cost. Re-spelling the name drops the qualifier, so the
    /// closure check is the thing that could have been loosened with it — a
    /// <c>Box&lt;string&gt;</c> must still be refused by <c>M.Box&lt;int&gt;</c>
    /// (`TOAST-0125`).
    /// </summary>
    [Fact]
    public async Task A_wrongly_closed_generic_is_still_refused()
    {
        await Assert.ThrowsAnyAsync<Exception>(
            () => RunAsync(Module + "var b: M.Box<int> = (new M.Box<string>(\"x\"))\n$b.V"));
    }

    /// <summary>
    /// And a qualified annotation still refuses an unrelated class, rather than matching
    /// anything whose bare name happens to resolve.
    /// </summary>
    [Fact]
    public async Task A_qualified_annotation_still_refuses_an_unrelated_class()
    {
        await Assert.ThrowsAnyAsync<Exception>(
            () => RunAsync(
                Module
                + "module N { export class Crate<T>(v: T) { prop V: T = $v } }\n"
                + "var b: M.Box<int> = (new N.Crate<int>(1))\n$b.V"));
    }
}
