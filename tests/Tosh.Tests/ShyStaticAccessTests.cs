using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// A class's own code can reach its `shy shared` members, methods included.
///
/// `TS-P2-103`. `shy shared func` could not be called from the class that
/// declared it, which made a private helper impossible in a `hermit class`: with
/// no instance there is no `$this` to carry the accessor, so the qualified name
/// is the only spelling available, and refusing it meant every helper had to be
/// public.
///
/// The rule itself already existed. `CanSeeShyStatic()` answers "is this class's
/// own code asking?" and was consulted for nested types and for static
/// properties — three places — but not for static methods. This is the `TS-P1-24`
/// shape: one rule, applied everywhere but the fourth place, so the surface
/// disagreed with itself and a `shy shared prop` worked where a `shy shared func`
/// did not.
/// </summary>
public class ShyStaticAccessTests
{
    private static async Task<string> RunAsync(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);
        var results = await engine.ExecuteToListAsync(source);
        return string.Join(",", results.Select(value => value?.ToString() ?? "null"));
    }

    private const string Bits = """
        hermit class Bits {
            shy shared func Value(v: int) -> int => ($v * 2)
            shy shared prop Secret = 7
            shared func Double(v: int) -> int => (Bits.Value($v))
            shared func Both() -> int => (Bits.Value(Bits.Secret))
        }

        """;

    /// <summary>The reported case: the helper a hermit class could not have.</summary>
    [Fact]
    public async Task A_shy_shared_func_is_callable_from_its_own_class()
        => Assert.Equal("42", await RunAsync(Bits + "Bits.Double(21)"));

    /// <summary>
    /// A shy method and a shy property in one expression. The property half always
    /// worked, so this is what proves the two now agree rather than merely that
    /// the method stopped throwing.
    /// </summary>
    [Fact]
    public async Task A_shy_func_and_a_shy_prop_agree_inside_the_class()
        => Assert.Equal("14", await RunAsync(Bits + "Bits.Both()"));

    [Fact]
    public async Task An_ordinary_class_reaches_its_own_shy_static()
        => Assert.Equal("hidden", await RunAsync(
            """
            class Outer {
                shy shared func Hidden() -> string => "hidden"
                shared func Own() -> string => (Outer.Hidden())
            }
            Outer.Own()
            """));

    /// <summary>
    /// From outside, still refused — the point of `shy`.
    /// </summary>
    [Fact]
    public async Task A_shy_shared_func_is_still_refused_from_outside()
    {
        var exception = await Assert.ThrowsAnyAsync<Exception>(
            () => new ToshEngine(ToshRuntime.CreateDefault().Language).ExecuteToListAsync(Bits + "Bits.Value(1)"));

        Assert.Contains("shy", exception.Message);
    }

    /// <summary>
    /// A nested class is outside, and that is deliberate rather than an oversight:
    /// it is what `CanSeeShyStatic` already did for static *properties*, measured
    /// before this change. Matching the existing rule keeps one answer for the
    /// whole surface; inventing a laxer one for methods alone would have put the
    /// disagreement back, pointing the other way.
    /// </summary>
    [Fact]
    public async Task A_nested_class_is_outside_for_methods_and_properties_alike()
    {
        var method = await Assert.ThrowsAnyAsync<Exception>(
            () => new ToshEngine(ToshRuntime.CreateDefault().Language).ExecuteToListAsync(
                """
                class Outer {
                    shy shared func Hidden() -> string => "h"
                    class Nested { shared func Reach() -> string => (Outer.Hidden()) }
                }
                Outer.Nested.Reach()
                """));

        var property = await Assert.ThrowsAnyAsync<Exception>(
            () => new ToshEngine(ToshRuntime.CreateDefault().Language).ExecuteToListAsync(
                """
                class Outer {
                    shy shared prop Hidden = "h"
                    class Nested { shared func Reach() -> string => (Outer.Hidden) }
                }
                Outer.Nested.Reach()
                """));

        Assert.Contains("shy", method.Message);
        Assert.Contains("shy", property.Message);
    }

    /// <summary>
    /// A non-shy static is unaffected, from inside and out.
    /// </summary>
    [Theory]
    [InlineData("Open.Visible(21)", "42")]
    [InlineData("Open.Wrapper(21)", "42")]
    public async Task An_ordinary_shared_func_is_unaffected(string body, string expected)
        => Assert.Equal(expected, await RunAsync(
            """
            hermit class Open {
                shared func Visible(v: int) -> int => ($v * 2)
                shared func Wrapper(v: int) -> int => (Open.Visible($v))
            }

            """ + body));

    /// <summary>
    /// The message for a genuinely absent static must not change into the shy one:
    /// widening the candidate filter could have made "not found" report as
    /// "is shy", which would send the reader looking for a visibility problem that
    /// does not exist.
    /// </summary>
    [Fact]
    public async Task A_missing_static_still_reports_as_missing()
    {
        var exception = await Assert.ThrowsAnyAsync<Exception>(
            () => new ToshEngine(ToshRuntime.CreateDefault().Language).ExecuteToListAsync(Bits + "Bits.Nope(1)"));

        Assert.Contains("was not found", exception.Message);
        Assert.DoesNotContain("shy", exception.Message);
    }
}
