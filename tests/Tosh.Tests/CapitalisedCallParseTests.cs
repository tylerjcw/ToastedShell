using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// Case does not decide how a call parses — <c>TOAST-0102</c>.
/// </summary>
/// <remarks>
/// <para>
/// <c>(lower ($x) 1 2 3)</c> parsed and <c>(Upper ($x) 1 2 3)</c> did not, reporting
/// <c>missing_pipeline_separator</c> — a message about pipelines for a call. Two functions
/// differing only in the case of their first letter parsed differently at the call site, which
/// made capitalisation a load-bearing part of the syntax that nothing documented and penalised
/// .NET-style naming specifically.
/// </para>
/// <para>
/// It also depended on <em>where</em> the callee was declared: a name the same file declares was
/// already carved out, so the identical call parsed in one file and failed once the definition
/// moved to another. Splitting a library across files makes that near-certain, which is how it
/// was found — renaming <c>series-of</c> to <c>SeriesOf</c> broke a file, and the workaround was
/// the comma form in 56 places.
/// </para>
/// <para>
/// The rule is now whitespace: a space between an unqualified name and its parenthesis says
/// command invocation, no space says call. That needs no knowledge of what the name is, which is
/// the point — the parser cannot see another file's declarations, and every rule that pretends
/// otherwise fails at a file boundary.
/// </para>
/// </remarks>
public sealed class CapitalisedCallParseTests
{
    private static async Task<string> RunAsync(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);
        var results = await engine.ExecuteToListAsync(source);
        return results.Count == 0 ? string.Empty : results[^1]?.ToString() ?? "null";
    }

    /// <summary>Callee and caller in separate module parts, as separate files would be.</summary>
    private const string SplitDeclaration = """
        export partial module T {
            export func lower(v, a, b, c) { return $v }
            export func Upper(v, a, b, c) { return $v }
        }

        export partial module T {
            export func GoLower(s) { return (lower ($s) 1 2 3) }
            export func GoUpper(s) { return (Upper ($s) 1 2 3) }
            export func GoComma(s) { return Upper(($s), 1, 2, 3) }
            export func GoTight(s) { return Upper($s, 1, 2, 3) }
        }
        """;

    [Theory]
    [InlineData("GoLower")]
    [InlineData("GoUpper")]
    [InlineData("GoComma")]
    [InlineData("GoTight")]
    public async Task Every_spelling_of_the_call_parses_and_runs(string entry)
    {
        Assert.Equal("5", await RunAsync($"{SplitDeclaration}\necho (T.{entry}(5))"));
    }

    [Fact]
    public async Task Case_alone_does_not_change_the_parse()
    {
        // The property, stated directly: the two differ only in the callee's first letter.
        Assert.Equal(
            await RunAsync($"{SplitDeclaration}\necho (T.GoLower(5))"),
            await RunAsync($"{SplitDeclaration}\necho (T.GoUpper(5))"));
    }

    [Theory]
    [InlineData("echo (System.Math.Max(3, 9))", "9")]
    [InlineData("echo (Sys.Math.Abs(-4))", "4")]
    [InlineData("echo (System.String.Concat(\"a\", \"b\"))", "ab")]
    public async Task A_qualified_static_call_is_unaffected(string source, string expected)
    {
        // These are the calls the capitalisation rule existed to recognise. They are qualified,
        // so they never reach the whitespace test.
        Assert.Equal(expected, await RunAsync(source));
    }

    [Fact]
    public async Task A_spaced_qualified_static_call_still_works()
    {
        // The whitespace rule is confined to unqualified names on purpose: a dotted name is a
        // static call whatever the spacing.
        Assert.Equal("9", await RunAsync("echo (System.Math.Max (3, 9))"));
    }

    [Fact]
    public async Task A_generic_static_call_is_unaffected()
    {
        // Asserting the *shape*, not a count: what matters is that the type-argument list still
        // reaches the call rather than ending the command.
        Assert.Equal("Int32[]", await RunAsync("echo ((System.Array.Empty<int>()).GetType().Name)"));
    }

    [Fact]
    public async Task A_locally_declared_capitalised_function_still_calls_tight()
    {
        Assert.Equal("7", await RunAsync("""
            func Foo(x) { return $x }
            echo (Foo(7))
            """));
    }
}
