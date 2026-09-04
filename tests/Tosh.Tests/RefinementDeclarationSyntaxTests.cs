using Tosh.Language;
using Tosh.Language.Parsing;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// The spellings a refinement type may be declared in — <c>TOAST-0112</c>.
/// </summary>
/// <remarks>
/// <para>
/// The brace body already existed as <c>type Name = Base { where … }</c>. What was missing is the
/// colon, so that a refinement reads the way <c>enum Level: int { … }</c> does — the language's
/// established spelling for "a declaration with an underlying type and a braced body".
/// </para>
/// <para>
/// The base type stays required, because it is the thing being refined. Left out, the parser says
/// so and shows both spellings, rather than reporting <c>Command 'type' is not a builtin</c> as it
/// used to.
/// </para>
/// </remarks>
public sealed class RefinementDeclarationSyntaxTests
{
    private static async Task<string> RunAsync(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);
        var results = await engine.ExecuteToListAsync(source);
        return results.Count == 0 ? string.Empty : results[^1]?.ToString() ?? "null";
    }

    [Theory]
    // The colon, spaced every way the lexer can hand it over: `A:` and `A:int` arrive glued to
    // the name token, `A` and `:` arrive apart.
    [InlineData("type A: int { where _ > 0 }")]
    [InlineData("type A : int { where _ > 0 }")]
    [InlineData("type A:int { where _ > 0 }")]
    [InlineData("type A: int where _ > 0")]
    // The original spellings, which must keep working.
    [InlineData("type A = int { where _ > 0 }")]
    [InlineData("type A = int where _ > 0")]
    public async Task Every_spelling_declares_the_same_refinement(string declaration)
    {
        Assert.Equal("True", await RunAsync($"{declaration}\necho (5 is A)"));
        Assert.Equal("False", await RunAsync($"{declaration}\necho (-1 is A)"));
    }

    [Fact]
    public async Task The_colon_spelling_carries_a_brace_body_with_a_coercer()
    {
        Assert.Equal("1", await RunAsync("""
            type Repaired: int {
                where _ > 0
                coerce (_ == 0 ? 1 : Math.abs(_))
            }
            echo (0 as Repaired)
            """));
    }

    [Fact]
    public async Task The_colon_spelling_works_over_a_non_numeric_base()
    {
        Assert.Equal("True", await RunAsync("""
            type Name: string { where _.Length > 2 }
            echo ("abcd" is Name)
            """));
    }

    [Fact]
    public async Task A_generic_alias_is_unaffected()
    {
        Assert.Equal("3", await RunAsync("""
            type NonEmpty<T> = list<T> where _.Count > 0
            var xs: NonEmpty<int> = [1, 2, 3]
            echo ($xs | count)
            """));
    }

    [Fact]
    public void A_missing_base_type_is_named_once()
    {
        // Before this it produced "Command 'type' is not a registered builtin", because the
        // lookahead required `=` and so never recognised the declaration at all.
        var parsed = ToshParser.Parse("type PosInt {\n    where _ > 0\n}", "test.tosh");

        var diagnostic = Assert.Single(parsed.Diagnostics);
        Assert.Equal("tosh.parser.expected_alias_base_type", diagnostic.Code);
        Assert.Contains("PosInt", diagnostic.Title, StringComparison.Ordinal);
        Assert.Contains("type PosInt: int", diagnostic.Help ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("type PosInt = int", diagnostic.Help ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_plain_alias_with_no_refinement_still_works()
    {
        Assert.Equal("True", await RunAsync("""
            type Count: int
            echo (5 is Count)
            """));
    }
}
