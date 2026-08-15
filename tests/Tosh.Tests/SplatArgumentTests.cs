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
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
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
            () => new ToshEngine(ToshRuntime.CreateDefault()).ExecuteToListAsync(
                Fixture + "(C.Three($f))"));
    }

    /// <summary>
    /// Pinned because it is pre-existing and easily mistaken for this item: a rest
    /// parameter spreads a list on its own. Nothing here changed it, and a future
    /// change to argument evaluation that did would show up as this test failing
    /// rather than as a puzzling behaviour difference somewhere else.
    /// </summary>
    [Fact]
    public async Task A_rest_parameter_already_spreads_a_bare_list()
        => Assert.Equal("3", await RunAsync(
            Fixture +
            """
            func CountArgs(vals...) -> int {
                var n = 0
                for v in $vals { $n = ($n + 1) }
                return $n
            }
            (CountArgs($f))
            """));
}
