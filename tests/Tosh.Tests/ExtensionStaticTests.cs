using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// A <c>static func</c> in an <c>extend</c> block is reachable as a static — <c>TOAST-0097</c>.
/// </summary>
/// <remarks>
/// <para>
/// It parsed, it was stored, and it matched nothing at the point of use — the failure mode
/// <c>TOAST-0016</c> had already fixed once, for <c>extend int</c>. Worse than the item
/// recorded: the modifier was discarded rather than unsupported, so the declaration was filed
/// in the instance table and answered <c>$value.name()</c> while <c>Type::name()</c> found
/// nothing. A declaration that is accepted has to be findable as what it was written as.
/// </para>
/// <para>
/// This is what let <c>Option::from</c> exist. <c>TOAST-0083</c> described that surface and it
/// could not be built: a union body takes variants only, and <c>extend</c> added instance
/// methods alone, so the conversion shipped as the free function <c>option-from</c>. Both
/// spellings are kept now — a bareword is what a pipeline wants.
/// </para>
/// </remarks>
public sealed class ExtensionStaticTests
{
    private static async Task<string> RunAsync(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);
        var results = await engine.ExecuteToListAsync(source);
        return results.Count == 0 ? "<none>" : results[^1]?.ToString() ?? "null";
    }

    private static async Task<string> CodeOfAsync(string source)
    {
        try
        {
            await RunAsync(source);
            return "<no diagnostic>";
        }
        catch (ToshDiagnosticException exception)
        {
            return exception.Diagnostics.Count == 0 ? "<no diagnostic>" : exception.Diagnostics[0].Code;
        }
    }

    private const string Extended =
        """
        class C { prop X = 1 }
        extend C { static func make() { return "hi" } }
        """;

    /// <summary>Both spellings reach it: they differ in what they read as, not what they resolve.</summary>
    [Theory]
    [InlineData("C::make()")]
    [InlineData("C.make()")]
    public async Task A_static_extension_is_reachable(string call)
        => Assert.Equal("hi", await RunAsync($"{Extended}\n{call}"));

    /// <summary>
    /// And is no longer reachable as an instance method, which is where it used to be filed.
    /// Being callable as the wrong thing is what made this worse than unsupported.
    /// </summary>
    [Fact]
    public async Task A_static_extension_is_not_an_instance_method()
        => Assert.NotEqual("hi", await CodeOfAsync($"{Extended}\nvar c = new C()\n$c.make()"));

    /// <summary>
    /// The case with no other route. A union body takes variants only, so before this there
    /// was nowhere at all to put a static on one.
    /// </summary>
    [Fact]
    public async Task A_union_can_be_given_a_static()
        => Assert.Equal(
            "5",
            await RunAsync(
                """
                union U { A(v), B }
                extend U { static func of(v) { return U::A($v) } }
                echo (match (U::of(5)) {
                    A(v) => $v
                    default => 0
                })
                """));

    /// <summary>
    /// The surface `TOAST-0083` described, which could not be built until now. Both spellings
    /// are kept and must agree.
    /// </summary>
    [Theory]
    [InlineData("(Option::from(null)).is-none()", "True")]
    [InlineData("(Option::from(5)).unwrap-or(0)", "5")]
    [InlineData("(option-from 5).unwrap-or(0)", "5")]
    public async Task The_option_conversion_answers_to_both_spellings(string call, string expected)
        => Assert.Equal(expected, await RunAsync($"echo ({call})"));

    /// <summary>
    /// An extension is consulted only where the type declined, so one that would collide
    /// could never run. Refused at the declaration, where the name can still be changed,
    /// rather than stored and silently beaten.
    /// </summary>
    [Theory]
    [InlineData("class K { static func make() { return 1 } }\nextend K { static func make() { return 2 } }")]
    [InlineData("extend string { static func Join() { return 1 } }")]
    public async Task Displacing_a_declared_static_is_refused(string source)
        => Assert.Equal("tosh.runtime.extension_static_conflict", await CodeOfAsync(source));

    /// <summary>A name the type does not declare is not a collision.</summary>
    [Fact]
    public async Task A_name_the_type_does_not_declare_is_accepted()
        => Assert.Equal(
            "x",
            await RunAsync("extend string { static func Shout(v) { return $v } }\nstring::Shout(\"x\")"));

    /// <summary>The type's own statics keep answering, extension table or not.</summary>
    [Fact]
    public async Task A_real_static_still_wins()
        => Assert.Equal(
            "a-b",
            await RunAsync("extend string { static func Shout(v) { return $v } }\nstring::Join(\"-\", [\"a\", \"b\"])"));

    /// <summary>Instance extensions are untouched — they were never the broken half.</summary>
    [Fact]
    public async Task An_instance_extension_still_works()
        => Assert.Equal(
            "2",
            await RunAsync("class D { prop X = 1 }\nextend D { func twice() { return 2 } }\nvar d = new D()\n$d.twice()"));
}
