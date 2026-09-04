using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// A refinement type derived from a sibling in the same module — <c>TOAST-0104</c>.
/// </summary>
/// <remarks>
/// <para>
/// A base type is resolved where the alias is <em>used</em>, and by then the declaring module's
/// scope has left the stack. So <c>export type Derived = Base where …</c> inside a module found
/// nothing for <c>Base</c>, and the alias silently ceased to exist — no diagnostic at the
/// declaration, the failure surfacing later at a consumer as "unknown type".
/// </para>
/// <para>
/// Found in the author's own <c>Types/StringTypes.tosh</c>, where a chain
/// <c>SingleLine → TrimmedString → {EmailLike, HttpUrl, SemVer, …}</c> meant seven of ten
/// declared types did not exist, for a month, with the file loading cleanly the whole time.
/// </para>
/// <para>
/// The alias now carries the module it was declared in, the same device
/// <c>ToshClassDefinition</c> uses around member invocation.
/// </para>
/// </remarks>
public sealed class ModuleRefinementBaseTests
{
    private static async Task<string> RunAsync(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);
        var results = await engine.ExecuteToListAsync(source);
        return results.Count == 0 ? string.Empty : results[^1]?.ToString() ?? "null";
    }

    private const string Nested = """
        module M {
            module T {
                export type Base        = string where _.Length > 0
                export type Unqualified = Base where _.Length < 10
                export type Qualified   = M.T.Base where _.Length < 10
            }
        }
        """;

    [Fact]
    public async Task An_unqualified_sibling_base_resolves()
    {
        Assert.Equal("hi", await RunAsync(Nested + "\nvar b: M.T.Unqualified = \"hi\"\necho $b"));
    }

    [Fact]
    public async Task The_qualified_spelling_still_works()
    {
        Assert.Equal("hi", await RunAsync(Nested + "\nvar a: M.T.Qualified = \"hi\"\necho $a"));
    }

    [Fact]
    public async Task The_refinement_still_rejects()
    {
        // Resolving the base must not quietly stop the predicate being applied.
        var error = await Assert.ThrowsAnyAsync<Exception>(async () =>
            await RunAsync(Nested + "\nvar b: M.T.Unqualified = \"far too long to pass\""));

        Assert.Contains("refinement", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_chain_four_levels_deep_resolves_and_rejects_at_the_right_link()
    {
        const string chain = """
            module ToastLib {
                module Types {
                    export type SingleLine    = string where not (_.Contains("\n"))
                    export type TrimmedString = SingleLine where (_ == _.Trim())
                    export type Slug          = TrimmedString { where _.Length > 2 }
                    export type Deep          = Slug where _.Length < 20
                }
            }
            """;

        Assert.Equal("short-slug", await RunAsync(chain + "\nvar d: ToastLib.Types.Deep = \"short-slug\"\necho $d"));

        var error = await Assert.ThrowsAnyAsync<Exception>(async () =>
            await RunAsync(chain + "\nvar e: ToastLib.Types.Deep = \"this-one-is-far-too-long\""));

        // The outermost link is the one that rejects, and the help names its predicate — so the
        // chain is being walked to the top rather than stopping at the first base that resolved.
        var diagnostic = Assert.Single(Assert.IsType<ToshDiagnosticException>(error).Diagnostics);
        Assert.Contains("_.Length < 20", diagnostic.Help ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_forward_referenced_base_still_works()
    {
        // Bases resolve lazily, so an alias may name a sibling declared after it. That is why the
        // base cannot simply be checked eagerly at the declaration.
        Assert.Equal("hi", await RunAsync("""
            module M {
                export type Derived = Base where _.Length < 10
                export type Base = string where _.Length > 0
            }

            var x: M.Derived = "hi"
            echo $x
            """));
    }

    [Fact]
    public async Task A_base_that_does_not_exist_names_itself_and_its_alias()
    {
        // Previously: "'x' uses unknown type annotation 'M.Broken'" — which sends the reader to
        // the consumer rather than to the declaration that is actually wrong.
        var error = await Assert.ThrowsAnyAsync<Exception>(async () => await RunAsync("""
            module M {
                export type Broken = NoSuchBase where _.Length > 0
            }

            var x: M.Broken = "hi"
            """));

        Assert.Contains("Broken", error.Message, StringComparison.Ordinal);
        Assert.Contains("NoSuchBase", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_ordinary_unknown_annotation_is_unchanged()
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(async () =>
            await RunAsync("var y: NoSuchTypeAtAll = \"hi\""));

        Assert.Contains("unknown type annotation", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_top_level_derivation_is_unaffected()
    {
        Assert.Equal("hi", await RunAsync("""
            type Base = string where _.Length > 0
            type Derived = Base where _.Length < 10
            var x: Derived = "hi"
            echo $x
            """));
    }
}
