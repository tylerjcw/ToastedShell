using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// <c>Sys</c> is a namespace alias for <c>System</c> — <c>TOAST-0078</c>.
/// </summary>
/// <remarks>
/// It read as one long before it was one: written fourteen times across the author's library,
/// where it resolved to <c>Interop+Sys</c>, a private runtime class, so every
/// <c>Sys.Math.Clamp</c> failed naming a type nobody had written.
/// </remarks>
public sealed class SysNamespaceAliasTests
{
    private static async Task<IReadOnlyList<object?>> RunAsync(string script)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);
        return await engine.ExecuteToListAsync(script);
    }

    [Theory]
    [InlineData("Sys.Math.Clamp(5, 1, 3)", "3")]
    [InlineData("Sys.Math.Round(2.7)", "3")]
    [InlineData("Sys.String.Join(\"-\", [\"a\", \"b\"])", "a-b")]
    public async Task Sys_names_the_system_namespace(string expression, string expected)
    {
        var results = await RunAsync($"echo ({expression})");
        Assert.Equal(expected, results[^1]?.ToString());
    }

    /// <summary>
    /// A prefix alias cannot take a name away: a class of one's own called <c>Sys</c> still
    /// wins, which is what keeps the author's <c>export hermit class Sys</c> working.
    /// </summary>
    [Fact]
    public async Task A_declared_type_still_outranks_the_alias()
    {
        var results = await RunAsync("""
            class Sys {
                shared func Init() { return "MINE" }
            }
            echo (Sys.Init())
            """);

        Assert.Equal("MINE", results[^1]?.ToString());
    }

    [Fact]
    public async Task System_is_unaffected()
    {
        var results = await RunAsync("echo (System.Math.Clamp(9, 1, 3))");
        Assert.Equal("3", results[^1]?.ToString());
    }

    /// <summary>
    /// The failure this replaces: a conversion problem reported as a missing type, because the
    /// "is it known?" check asked the CLR resolver for <c>list&lt;Token&gt;</c> and it answered
    /// only while <c>Token</c> matched an internal type.
    /// </summary>
    [Fact]
    public async Task A_collection_over_a_declared_type_reports_conversion_not_absence()
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(async () => await RunAsync("""
            class Token(k: string) { prop K: string = $k }
            class Other { }
            func make() -> list<Token> { return [new Other()] }
            echo ((make) | count)
            """));

        Assert.Contains("could not be converted", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("unknown type", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_collection_over_a_declared_type_still_works()
    {
        var results = await RunAsync("""
            class Token(k: string) { prop K: string = $k }
            func make() -> list<Token> { return [new Token("a")] }
            echo ((make) | count)
            """);

        Assert.Equal("1", results[^1]?.ToString());
    }
}
