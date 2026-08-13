using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// A hole resolves names against the same scope as the code around it.
///
/// `TS-P2-114`. A module's sibling function was unreachable from an
/// interpolation hole: `module M { func Helper(x) => …  func Caller() {
/// writeline $"{Helper(3)}" } }` failed, while the same call one line up
/// succeeded, and `{M.Helper(3)}`, `{$f(3)}` and a top-level `{Plain(3)}` all
/// worked inside holes.
///
/// A hole's text is re-parsed at runtime as a pure expression, where
/// <c>Name(args)</c> builds a qualified path rather than a command — so it went
/// straight to CLR resolution and never consulted the scope. The message
/// depended on what the CLR happened to hold: `Helper` collides with a real type
/// and produced "Construct instances with 'new Helper(...)'", while `Zqx`
/// produced "Unable to resolve .NET access path". Two messages, one cause; the
/// first sent me looking for a name collision that was not the problem.
/// </summary>
public class InterpolationHoleCallTests
{
    private static async Task<string> RunAsync(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync(source);
        return string.Join(",", results.Select(value => value?.ToString() ?? "null"));
    }

    private const string Module = """
        module M {
            func Helper(x: int) -> string => $"h{$x}"
            func Zqx(x: int) -> string => $"z{$x}"

        """;

    /// <summary>The reported case.</summary>
    [Fact]
    public async Task A_module_sibling_is_callable_from_a_hole()
        => Assert.Equal("h3", await RunAsync(
            Module + "    func Probe() -> string => $\"{Helper(3)}\"\n}\nM.Probe()"));

    /// <summary>
    /// The same call under a name that collides with nothing in the CLR. Testing
    /// only `Helper` would leave it ambiguous whether the fix addressed the
    /// collision or the resolution.
    /// </summary>
    [Fact]
    public async Task A_sibling_whose_name_matches_no_clr_type_works_too()
        => Assert.Equal("z4", await RunAsync(
            Module + "    func Probe() -> string => $\"{Zqx(4)}\"\n}\nM.Probe()"));

    /// <summary>
    /// The three spellings that already worked, which the fix must not disturb.
    /// </summary>
    [Fact]
    public async Task The_spellings_that_already_worked_still_do()
    {
        Assert.Equal("h5", await RunAsync(
            Module + "    func Probe() -> string => $\"{M.Helper(5)}\"\n}\nM.Probe()"));

        Assert.Equal("h6", await RunAsync(
            Module + "    func Probe() -> string { var f = &Helper\n        return $\"{$f(6)}\" }\n}\nM.Probe()"));

        Assert.Equal("p7", await RunAsync(
            "func Plain(x: int) -> string => $\"p{$x}\"\n$\"{Plain(7)}\""));
    }

    /// <summary>
    /// The non-regression that matters most: a dotted path is a qualified name and
    /// must still reach the CLR. The scope lookup is restricted to single-segment
    /// paths for exactly this reason.
    /// </summary>
    [Fact]
    public async Task A_clr_static_call_in_a_hole_is_unaffected()
        => Assert.Equal("9", await RunAsync("$\"{Math.Max(1, 9)}\""));

    /// <summary>
    /// A name that really is a type still gets the message telling you to
    /// construct it. The scope lookup runs first, so a type with no function
    /// shadowing it must fall through unchanged.
    /// </summary>
    [Fact]
    public async Task A_type_name_still_says_to_construct_it()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var exception = await Assert.ThrowsAnyAsync<Exception>(
            () => engine.ExecuteToListAsync("class Widget { prop N = 1 }\n$\"{Widget(3)}\""));

        Assert.Contains("new Widget", exception.Message);
    }

    /// <summary>
    /// Both parses now share one invocation, so the multi-value diagnostic has to
    /// survive on the path that was rerouted — a helper that dropped it would
    /// turn a reported error into a silently discarded value.
    /// </summary>
    [Fact]
    public async Task An_invocation_yielding_several_values_is_still_rejected()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var exception = await Assert.ThrowsAsync<ToshDiagnosticException>(
            () => engine.ExecuteToListAsync("func many() { 1\n2 }\n(many() + 0)"));

        Assert.Contains(
            exception.Diagnostics,
            d => d.Code == "tosh.runtime.callable_invocation_requires_single_value");
    }

    /// <summary>`TS-P2-01`'s case, which shares the rerouted invocation helper.</summary>
    [Fact]
    public async Task A_function_still_composes_in_an_operator_expression()
        => Assert.Equal("42", await RunAsync("func f() => 41\n(f() + 1)"));
}
