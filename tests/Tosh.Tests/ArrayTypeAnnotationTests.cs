using Tosh.Language;
using Tosh.Language.Parsing;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// <c>T[]</c> is a CLR array type in every annotation position — <c>TS-P2-69</c>.
/// </summary>
/// <remarks>
/// <para>
/// It was rejected everywhere: <c>func f() -&gt; string[]</c> reported
/// <c>expected_block</c>, <c>func f(x: string[])</c> reported
/// <c>missing_function_parameter_separator</c>, and <c>var y: string[] = […]</c> reported
/// "Command 'var' was not found" — a message about <c>var</c> for a defect in the type
/// annotation, because the declaration lookahead knew <c>&lt;…&gt;</c> and <c>?</c> but not
/// <c>[]</c> and gave up before parsing.
/// </para>
/// <para>
/// <c>string[]</c> resolves to <c>System.String[]</c>, which makes it the typed form of
/// the <c>array</c> alias the language already had for <c>System.Object[]</c>;
/// <c>list&lt;string&gt;</c> is unchanged and still a <c>List&lt;string&gt;</c>.
/// </para>
/// <para>
/// Only an <em>empty</em> bracket pair is a type suffix, which is what lets it coexist
/// with the native buffer suffix: in a native signature <c>buffer[256]</c> and
/// <c>double[3]</c> declare a fixed inline capacity, and those still parse as before
/// because the suffix loop requires an immediate <c>]</c>.
/// </para>
/// </remarks>
public sealed class ArrayTypeAnnotationTests
{
    private static void ParsesClean(string source)
    {
        var result = ToshParser.Parse(source, "<probe>");

        Assert.True(
            result.Diagnostics.Count == 0,
            source + "\n  " + string.Join(
                "\n  ",
                result.Diagnostics.Select(d => $"{d.Code} — {d.Title}")));
    }

    [Theory]
    [InlineData("func f() -> string[] { return [\"a\"] }")]
    [InlineData("func f(x: string[]) -> int { return 1 }")]
    [InlineData("var y: string[] = [\"a\"]")]
    [InlineData("var y:string[] = [\"a\"]")]
    [InlineData("class C { prop Names: string[] = [] }")]
    [InlineData("var j: string[][] = [[\"a\"]]")]
    [InlineData("var n: string[]? = null")]
    [InlineData("func f(x: int[], y: string[]) -> int { return 1 }")]
    public void An_array_annotation_parses_in_every_position(string source) => ParsesClean(source);

    [Fact]
    public void The_native_buffer_suffix_still_means_a_capacity()
    {
        // The tension this had to resolve. A bracket in native parameter position holds a
        // fixed inline capacity, not an array type, and only an empty pair is the latter.
        ParsesClean("native func nf(b: buffer[256]) -> int from \"libc.so.6\"");
        ParsesClean("native func nf(d: double[3]) -> int from \"libc.so.6\"");
    }

    // ── What it resolves to ────────────────────────────────────────────────────

    [Theory]
    [InlineData("string[]", "System.String[]")]
    [InlineData("int[]", "System.Int32[]")]
    [InlineData("string[][]", "System.String[][]")]
    [InlineData("System.IO.Path[]", "System.IO.Path[]")]
    public void An_array_name_resolves_to_the_array_of_its_element(string name, string expected)
    {
        Assert.Equal(expected, new DotNetTypeResolver().Resolve(name)?.FullName);
    }

    [Fact]
    public void The_element_follows_the_same_precedence_as_a_bare_name()
    {
        // Resolved by taking the suffix off and asking for the element, so `TS-P2-66`'s
        // import precedence applies to `SpinLock[]` exactly as it does to `SpinLock`
        // rather than through a second lookup path.
        Assert.Equal(
            "System.Threading.SpinLock[]",
            new DotNetTypeResolver().Resolve("SpinLock[]")?.FullName);
    }

    [Fact]
    public void A_nonexistent_element_still_resolves_to_nothing()
    {
        Assert.Null(new DotNetTypeResolver().Resolve("NoSuchTypeAnywhere[]"));
    }

    // ── What it produces at runtime ────────────────────────────────────────────

    [Fact]
    public async Task A_cast_produces_a_real_clr_array()
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime.Language);

        var results = await engine.ExecuteToListAsync(
            """
            var t = (cast string[] ["a", "b"])
            $t.GetType().FullName
            """);

        Assert.Equal("System.String[]", Assert.Single(results)?.ToString());
    }

    [Fact]
    public async Task An_annotated_variable_holds_that_array()
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime.Language);

        var results = await engine.ExecuteToListAsync(
            """
            var y: string[] = ["a", "b"]
            $y.GetType().FullName
            """);

        Assert.Equal("System.String[]", Assert.Single(results)?.ToString());
    }

    [Fact]
    public async Task The_generic_list_spelling_is_untouched()
    {
        // `list<T>` was the only spelling before this and keeps its own meaning: a
        // List<T>, not an array. Adding `T[]` must not quietly redefine it.
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime.Language);

        var results = await engine.ExecuteToListAsync(
            """
            var l = (cast list<string> ["a"])
            $l.GetType().Name
            """);

        Assert.Equal("List`1", Assert.Single(results)?.ToString());
    }
}
