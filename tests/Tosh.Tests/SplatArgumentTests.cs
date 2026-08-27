using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// A splat spreads a list into a call's parameters, whatever the call is spelled
/// like.
///
/// `TS-P2-104`. Filed as "rejected wherever it would be useful", which measured
/// as narrower and more specific: a **bare-name** call spread fine, and every
/// *dotted* call reported "Unsupported argument syntax: SplatArgumentSyntax" —
/// instance, static, module-qualified and CLR alike.
///
/// The cause was two argument evaluators where only one knew the language had
/// spreading. The bare-name path used the splat-aware one; constructors, method
/// calls and qualified calls used a second walker that produced one value per
/// syntax node. They are now one implementation, so a future argument form cannot
/// reach half the language again.
/// </summary>
public class SplatArgumentTests
{
    private static async Task<string> RunAsync(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);
        var results = await engine.ExecuteToListAsync(source);
        return string.Join(",", results.Select(value => value?.ToString() ?? "null"));
    }

    private const string Fixture = """
        func Sum(vals...) -> int {
            var a = 0
            for v in $vals { $a = ($a + $v) }
            return $a
        }

        class C {
            func Add(vals...) -> int {
                var a = 0
                for v in $vals { $a = ($a + $v) }
                return $a
            }

            static func SAdd(vals...) -> int {
                var a = 0
                for v in $vals { $a = ($a + $v) }
                return $a
            }

            static func Three(a: int, b: int, c: int) -> int => ($a + $b + $c)
        }

        module M {
            export func MAdd(vals...) -> int {
                var a = 0
                for v in $vals { $a = ($a + $v) }
                return $a
            }
        }

        var obj = new C()
        var f = [1, 2, 4]
        var empty = []

        """;

    /// <summary>
    /// The four dotted spellings that failed, plus the bare one that always
    /// worked — kept together because the bare case is what made the others look
    /// like a general rejection rather than a split.
    /// </summary>
    [Theory]
    [InlineData("(Sum(...$f))", "7")]
    [InlineData("($obj.Add(...$f))", "7")]
    [InlineData("(C.SAdd(...$f))", "7")]
    [InlineData("(M.MAdd(...$f))", "7")]
    [InlineData("(String.Join(\"-\", ...$f))", "1-2-4")]
    public async Task A_splat_spreads_into_every_kind_of_call(string body, string expected)
        => Assert.Equal(expected, await RunAsync(Fixture + body));

    /// <summary>A fixed-arity callee, which the item names alongside the variadic one.</summary>
    [Fact]
    public async Task A_splat_fills_a_fixed_arity_callee()
        => Assert.Equal("7", await RunAsync(Fixture + "(C.Three(...$f))"));

    /// <summary>An empty list contributes nothing rather than one null.</summary>
    [Fact]
    public async Task An_empty_splat_contributes_no_arguments()
        => Assert.Equal("0", await RunAsync(Fixture + "(M.MAdd(...$empty))"));

    /// <summary>Ordinary arguments may lead, and keep their positions.</summary>
    [Theory]
    [InlineData("(M.MAdd(10, ...$f))", "17")]
    [InlineData("(String.Join(\"-\", ...$empty))", "")]
    public async Task A_splat_composes_with_leading_arguments(string body, string expected)
        => Assert.Equal(expected, await RunAsync(Fixture + body));

    /// <summary>
    /// The control that actually distinguishes a splat from ordinary passing —
    /// and the first attempt at it was wrong, which is worth recording.
    ///
    /// A list handed to a **rest** parameter already spreads without any splat:
    /// `CountArgs($f)` counted 3, not 1, and it did so before this change too. So
    /// a variadic callee cannot tell the two apart and proves nothing. A
    /// **fixed-arity** callee can: it refuses the bare list and accepts the
    /// splatted one, which is exactly the behaviour under test.
    /// </summary>
    [Fact]
    public async Task A_fixed_arity_callee_refuses_a_bare_list_and_accepts_a_splat()
    {
        Assert.Equal("7", await RunAsync(Fixture + "(C.Three(...$f))"));

        await Assert.ThrowsAnyAsync<Exception>(
            () => new ToshEngine(ToshRuntime.CreateDefault().Language).ExecuteToListAsync(
                Fixture + "(C.Three($f))"));
    }

    /// <summary>
    /// A bare list is **one** argument to a rest parameter; `...` is what spreads
    /// it. That is what makes the splat worth having.
    ///
    /// This assertion is the reverse of the one it replaces, and the story is worth
    /// keeping. The original pinned "a rest parameter already spreads a bare list"
    /// after measuring `CountArgs($f)` as 3 — before this change as well as after —
    /// and concluded the behaviour was pre-existing and deliberate. It was
    /// pre-existing and it was `TS-P2-113`: the binding always passed one argument,
    /// and the `for` loop *inside* the function expanded it a second time, so the
    /// count looked like a spread. Fixing the double expansion made the pin fail,
    /// which is how the misreading was caught.
    ///
    /// `$vals.Count` is asserted rather than a loop count, because a loop over the
    /// rest parameter is precisely what was miscounting.
    /// </summary>
    [Fact]
    public async Task A_bare_list_is_one_argument_and_a_splat_is_many()
    {
        const string probe = """
            func Probe(vals...) -> int { return $vals.Count }
            var f = [1, 2, 3]

            """;

        Assert.Equal("1", await RunAsync(probe + "(Probe($f))"));
        Assert.Equal("3", await RunAsync(probe + "(Probe(...$f))"));
    }

    // ── the two parser gaps, `TS-P2-104`'s remaining half ──────────────────────

    /// <summary>
    /// A constructor spreads exactly as a method does.
    /// </summary>
    /// <remarks>
    /// The constructor argument loop is its own and had no splat branch — the same "two
    /// walkers, one of which knows the language has spreading" shape this item's first half
    /// fixed in the *evaluator*, reappearing in the parser. Without it `...$pair` arrived as
    /// the **literal string** `"...$pair"`, which is why the failure read as a constructor
    /// arity error rather than as anything to do with splatting.
    /// </remarks>
    [Fact]
    public async Task A_constructor_spreads_a_splatted_list()
        => Assert.Equal("2", await RunAsync(
            """
            class SplatP {
                prop B: int = 0
                SplatP(a: int, b: int) { $this.B = $b }
            }
            var pair = [1, 2]
            (new SplatP(...$pair)).B
            """));

    /// <summary>
    /// A collection *literal* can be splatted, not only a variable holding one.
    /// </summary>
    /// <remarks>
    /// The lookahead required the target to be glued into the same token, and a `[` breaks
    /// the bareword — so `...` arrived alone and was not recognised at all. It is now
    /// accepted when an opening delimiter follows, narrowly: a bare `...` is also a
    /// rest-parameter marker and a native binding's variadic tail, and neither is followed
    /// by one.
    /// </remarks>
    [Theory]
    [InlineData("(SplatSum(...[1, 2, 3]))", "6")]
    [InlineData("(SplatSum(...[]))", "0")]
    public async Task A_collection_literal_can_be_splatted(string call, string expected)
        => Assert.Equal(expected, await RunAsync(
            "func SplatSum(vals...) -> int {\n    var total = 0\n    for v in $vals { $total = ($total + $v) }\n    return $total\n}\n" + call));

    /// <summary>
    /// A bare `...` that is *not* a splat still means what it meant — a rest parameter.
    /// This is the control for widening the lookahead.
    /// </summary>
    [Fact]
    public async Task A_rest_parameter_is_unaffected()
        => Assert.Equal("3", await RunAsync(
            "func SplatCount(vals...) -> int { return $vals.Count }\nSplatCount 1 2 3"));

    /// <summary>
    /// Ordinary leading arguments still combine with a splat.
    /// </summary>
    [Fact]
    public async Task Leading_arguments_still_combine_with_a_splat()
        => Assert.Equal("12", await RunAsync(
            """
            func SplatLead(a: int, rest...) -> int { return ($a + $rest.Count) }
            var v = [1, 2]
            SplatLead(10, ...$v)
            """));
}
