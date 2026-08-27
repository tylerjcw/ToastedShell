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
        return (value, engine.Shell().Formatter.Format(value));
    }

    /// <summary>
    /// Renders <paramref name="source"/>, feeds the rendering back in, and
    /// requires the second rendering to match. Formatting being a fixed point is
    /// the strongest check that does not need value equality, which several of
    /// these types do not usefully provide.
    /// </summary>
    private static async Task AssertRoundTripsAsync(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);

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

    /// <summary>
    /// A root collection renders like any other — **the type header is gone**.
    /// </summary>
    /// <remarks>
    /// This asserted `Int32[] [ 1, 2, 3 ]` until 2026-08-17, as the other half of the
    /// `TS-P3-10` decision: at the root the element type was held to be the informative
    /// part, and the rendering display rather than source.
    ///
    /// `TOAST-0014` reverses it. `Format` is a *language* operation now — it is what
    /// `$"{x}"`, `tee`, `template` and a compiled program's stdout produce — and a BCL type
    /// name has no place in a string a portable program builds. The table view is
    /// unaffected: `DisplayEngine` builds its own structure and never asks the formatter
    /// for a container, which is why the header was invisible in practice.
    ///
    /// The round-trip property is **wider** for it: a root collection now parses back and
    /// re-renders identically, where before it was a documented exception.
    /// </remarks>
    [Fact]
    public async Task A_root_collection_no_longer_carries_a_type_header()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);
        var (_, text) = await EvaluateAndFormatAsync(engine, "[1, 2, 3]");

        Assert.Equal("[1, 2, 3]", text);
        await AssertRoundTripsAsync("[1, 2, 3]");
    }

    [Fact]
    public async Task A_nested_collection_drops_it()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);
        var (_, text) = await EvaluateAndFormatAsync(engine, "{| v = [1, 2, 3] |}");

        Assert.Equal("{| v = [1, 2, 3] |}", text);
    }

    /// <summary>
    /// A nested container stays on one line — **the detail style no longer expands
    /// containers**.
    /// </summary>
    /// <remarks>
    /// This asserted a multi-line, once-per-level indented rendering with `Int32[][]`
    /// headers until 2026-08-17. `TOAST-0014` makes `Format` a language operation, and a
    /// rendered value must be safe to put on a stream: the multi-line form wrote newlines
    /// into a redirected file, so `cmd out> f | wc -l` counted a value's *shape* rather
    /// than its values.
    ///
    /// Depth is still bounded — see `ToastRendererTests.Depth_is_bounded` — so a deep
    /// structure elides rather than becoming an unreadable line.
    /// </remarks>
    [Fact]
    public async Task A_nested_container_stays_on_one_line()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);
        var value = Assert.Single(await engine.ExecuteToListAsync("[[1, 2], [3]]"));

        var text = engine.Shell().Formatter.Format(
            value,
            new ObjectFormattingOptions(ObjectRenderStyle.Detail));

        Assert.Equal("[[1, 2], [3]]", text);
        Assert.DoesNotContain("\n", text);
    }

    [Fact]
    public async Task The_property_catches_a_rendering_that_is_not_valid_source()
    {
        // Negative control. `{ a = 1 }` is what records rendered as before the
        // TS-P2-25 follow-up; feeding it back now fails to parse, which is the
        // failure this property is built to produce.
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);

        await Assert.ThrowsAnyAsync<Exception>(
            async () => await engine.ExecuteToListAsync("{ a = 1 }"));
    }
}
