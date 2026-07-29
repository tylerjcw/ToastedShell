using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// Property: whatever the formatter renders for a value must parse back as
/// ToastScript and format identically.
/// </summary>
/// <remarks>
/// <para>
/// This exists because of a defect no formatter test could have caught. After
/// <c>TS-P2-25</c> gave records and dicts their own delimiters, the formatter
/// kept rendering them as <c>{ a = 1 }</c> and <c>{ "k" =&gt; 1 }</c> — the
/// pre-decision spellings, which a block-only <c>{</c> now rejects. The suite
/// stayed green throughout: every formatter test asserts what the formatter
/// *produces*, and none asserted that what it produces is valid *input*.
/// </para>
/// <para>
/// Format-then-reparse closes that gap for every value type at once, rather than
/// one assertion per rendering. It is the same shape as the drift guards this
/// programme has come to rely on: state the property, let it find the instances.
/// </para>
/// <para>
/// Scope, established by running the property rather than assumed. Under
/// <c>TS-P3-10</c> a collection keeps its CLR type header only when it is the
/// whole result (<c>Int32[] [ 1, 2, 3 ]</c>), where the element type is
/// informative and the rendering is display rather than source; nested, it
/// renders <c>[ 1, 2 ]</c> and round-trips. So a root-level array is still
/// outside the property, deliberately, and arrays are exercised here in the
/// nested position where the contract is offered — the same split strings
/// already had, and for the same reason.
/// </para>
/// <para>
/// A bare string also renders unquoted at the root (<c>abc</c>, not
/// <c>"abc"</c>) because that is what display wants, so strings are exercised
/// nested inside a container, where the quoted form is used.
/// </para>
/// </remarks>
public sealed class FormatRoundTripTests
{
    private static async Task<(object? Value, string Text)> EvaluateAndFormatAsync(
        ToshEngine engine,
        string source)
    {
        var results = await engine.ExecuteToListAsync(source);
        var value = Assert.Single(results);
        return (value, engine.Runtime.Formatter.Format(value));
    }

    /// <summary>
    /// Renders <paramref name="source"/>, feeds the rendering back in, and
    /// requires the second rendering to match. Formatting being a fixed point is
    /// the strongest check that does not need value equality, which several of
    /// these types do not usefully provide.
    /// </summary>
    private static async Task AssertRoundTripsAsync(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var first = await EvaluateAndFormatAsync(engine, source);

        // Fails loudly rather than silently if the rendering is not parseable —
        // which is exactly the defect this guards.
        var second = await EvaluateAndFormatAsync(engine, first.Text);

        Assert.Equal(first.Text, second.Text);
    }

    [Theory]
    // Collections and the paired literals.
    [InlineData("{| a = 1, b = 2 |}")]
    [InlineData("{||}")]
    [InlineData("{| outer = {| inner = 1 |} |}")]
    [InlineData("{% \"k\" => 1, \"j\" => 2 %}")]
    [InlineData("{%%}")]
    [InlineData("{: 1, 2, 3 :}")]
    [InlineData("{::}")]
    // Scalars whose rendering should be source-like.
    [InlineData("1")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("true")]
    [InlineData("null")]
    public async Task Rendered_values_parse_back_and_render_identically(string source)
    {
        await AssertRoundTripsAsync(source);
    }

    [Theory]
    // Nested inside a record, which forces the quoted/nested rendering for the
    // element and exercises the container in the same pass.
    [InlineData("\"abc\"")]
    [InlineData("\"with space\"")]
    [InlineData("\"\"")]
    [InlineData("{| inner = 1 |}")]
    [InlineData("{: 1, 2 :}")]
    // Nested collections lost their type header in TS-P3-10, which is what
    // brings them inside the property; the root form still keeps it.
    [InlineData("[1, 2, 3]")]
    [InlineData("[[1, 2], [3]]")]
    [InlineData("[\"a\", \"b\"]")]
    [InlineData("true")]
    [InlineData("null")]
    [InlineData("1.5")]
    public async Task Values_nested_in_a_record_round_trip(string element)
    {
        await AssertRoundTripsAsync($"{{| v = {element} |}}");
    }

    [Fact]
    public async Task A_root_collection_keeps_its_type_header()
    {
        // The other half of the TS-P3-10 decision, pinned so the header is not
        // dropped everywhere by a later reading of this file: at the root the
        // element type is the informative part, and the rendering is display.
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var (_, text) = await EvaluateAndFormatAsync(engine, "[1, 2, 3]");

        Assert.Equal("Int32[] [ 1, 2, 3 ]", text);
    }

    [Fact]
    public async Task A_nested_collection_drops_it()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var (_, text) = await EvaluateAndFormatAsync(engine, "{| v = [1, 2, 3] |}");

        Assert.Equal("{| v = [ 1, 2, 3 ] |}", text);
    }

    [Fact]
    public async Task Nested_containers_indent_once_per_level()
    {
        // A container re-indents every line of every item, so a nested one that
        // also indented by its own depth was counted twice. Asserted on the
        // detail style, which is the only place the arithmetic is visible.
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var value = Assert.Single(await engine.ExecuteToListAsync("[[1, 2], [3]]"));

        var text = engine.Runtime.Formatter.Format(
            value,
            new ObjectFormattingOptions(ObjectRenderStyle.Detail));

        Assert.Equal(
            """
            Int32[][] [
              [
                1
                2
              ]
              [
                3
              ]
            ]
            """.ReplaceLineEndings("\n"),
            text.ReplaceLineEndings("\n"));
    }

    [Fact]
    public async Task The_property_catches_a_rendering_that_is_not_valid_source()
    {
        // Negative control. `{ a = 1 }` is what records rendered as before the
        // TS-P2-25 follow-up; feeding it back now fails to parse, which is the
        // failure this property is built to produce.
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        await Assert.ThrowsAnyAsync<Exception>(
            async () => await engine.ExecuteToListAsync("{ a = 1 }"));
    }
}
