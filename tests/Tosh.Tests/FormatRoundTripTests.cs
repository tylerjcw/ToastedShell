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
/// Scope, established by running the property rather than assumed. Records,
/// dicts and sets have bespoke source-like renderings; arrays and lists fall to
/// the generic path and render with a CLR type header
/// (<c>Int32[] [ 1, 2, 3 ]</c>), which does not pretend to be source. So
/// round-trip is not a contract the formatter offers across the board, and this
/// asserts it only where it is offered. The array inconsistency is recorded as
/// an observation, not silently excluded — see the stabilization log.
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
    [InlineData("true")]
    [InlineData("null")]
    [InlineData("1.5")]
    public async Task Values_nested_in_a_record_round_trip(string element)
    {
        await AssertRoundTripsAsync($"{{| v = {element} |}}");
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
