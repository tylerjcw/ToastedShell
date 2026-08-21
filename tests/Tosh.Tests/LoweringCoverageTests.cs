using Tosh.Compiler.IR;
using Tosh.Language;
using Tosh.Language.Binding;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// Every form the parser accepts also lowers to IR — `TOAST-0040`.
/// </summary>
/// <remarks>
/// <para>
/// Lowering and emitting are different questions. A construct the compiler cannot emit is
/// an ordinary gap; a construct that makes the *lowerer* throw produces no IR for the whole
/// file, and everything downstream — the emitter, and any tool that reads the tree — sees
/// nothing at all.
/// </para>
/// <para>
/// Both cases here were found by measuring rather than by a bug report: a reflective walk
/// over the repository and the author's own library reported 53 of 57 files lowering, and
/// the four failures were the ones that bind a native surface.
/// </para>
/// </remarks>
public sealed class LoweringCoverageTests
{
    private static BoundUnit Lower(string source)
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);
        var parse = engine.Parse(source, "<lowering>");
        Assert.True(parse.Diagnostics.Count == 0, $"parse errors: {string.Join(", ", parse.Diagnostics)}");
        return Lowerer.Lower(parse, runtime.Commands);
    }

    private static IEnumerable<BoundNode> Walk(BoundNode node)
    {
        yield return node;

        foreach (var property in node.GetType().GetProperties())
        {
            if (property.GetIndexParameters().Length > 0) { continue; }

            object? value;
            try { value = property.GetValue(node); } catch { continue; }

            switch (value)
            {
                case BoundNode child:
                    foreach (var d in Walk(child)) { yield return d; }
                    break;
                case System.Collections.IEnumerable seq and not string:
                    foreach (var item in seq)
                    {
                        if (item is BoundNode listChild)
                        {
                            foreach (var d in Walk(listChild)) { yield return d; }
                        }
                    }
                    break;
            }
        }
    }

    /// <summary>
    /// A `bind` block inside a class lowers instead of throwing.
    /// </summary>
    /// <remarks>
    /// It threw `Unknown class member kind: ClassBindMemberSyntax`, so `Sdl.tosh`,
    /// `Gl.tosh`, `Gtk.tosh` and `System.tosh` produced no IR at all. Emitting one is still
    /// out of scope — bind blocks are not CLR-emittable and stay Tier 3 — which is exactly
    /// the distinction that was being conflated.
    /// </remarks>
    [Fact]
    public void A_bind_block_in_a_class_lowers()
    {
        const string Source = """
            class NativeBox {
                bind libc {
                    func getpid() -> int
                }
            }
            """;

        var unit = Lower(Source);
        var bindMembers = Walk(unit.Root).OfType<BoundClassBindMember>().ToArray();

        Assert.Single(bindMembers);
        Assert.Equal("libc", bindMembers[0].Bind.ModuleName);
        Assert.Contains(bindMembers[0].Bind.Functions, f => f.Name == "getpid");
    }

    /// <summary>
    /// `...` in pipeline-stage position lowers, and is not a dynamic node.
    /// </summary>
    /// <remarks>
    /// `TOAST-0032` added the stage form and taught only the interpreter. The lowerer made
    /// it a `BoundDynamicExpression`, and the emitter then refused the entire unit —
    /// "dynamic argument expressions are not yet emitted", no output written.
    ///
    /// That is worse than an ordinary gap, because `...` is the spelling `TOAST-0028` and
    /// `TOAST-0039` tell people to migrate onto. Code written against the current
    /// collection-shape rule could not be compiled.
    /// </remarks>
    [Fact]
    public void A_pipeline_head_spread_lowers()
    {
        var unit = Lower("var xs = [1, 2, 3]\nvar n: int = (...$xs | count)");
        var nodes = Walk(unit.Root).ToArray();

        Assert.Single(nodes.OfType<BoundSpreadElement>());
        Assert.Empty(nodes.OfType<BoundDynamicExpression>());
    }

    /// <summary>
    /// The neighbouring spread forms were already lowered, and still are.
    /// </summary>
    /// <remarks>
    /// The control. A spread inside an array literal is `BoundArrayLiteralItem.IsSpread`,
    /// and argument position is a splat — neither is the stage form, and routing all three
    /// through one node would have been the obvious wrong fix.
    /// </remarks>
    [Fact]
    public void The_other_spread_forms_are_unchanged()
    {
        var inArray = Walk(Lower("var xs = [1, 2]\nvar ys = [...$xs, 3]").Root).ToArray();
        Assert.Contains(inArray.OfType<BoundArrayLiteralItem>(), i => i.IsSpread);
        Assert.Empty(inArray.OfType<BoundDynamicExpression>());

        var inArgument = Walk(Lower("var xs = [1, 2]\necho ...$xs").Root).ToArray();
        Assert.Empty(inArgument.OfType<BoundDynamicExpression>());
    }
}
