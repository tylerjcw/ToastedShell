using Tosh.LanguageServices;

namespace Tosh.Tests;

/// <summary>
/// A pipeline hover appears for a pipeline, and not for every nearby <c>|</c> — <c>TOAST-0109</c>.
/// </summary>
/// <remarks>
/// The hover entry point accepts any <c>|</c> or <c>&gt;</c> within three characters of the
/// cursor as a pipeline or redirect. A great deal is neither: the <c>|</c> of a record literal's
/// <c>{|</c> and <c>|}</c>, of an or-pattern, of <c>||</c>, of the comprehension separator; and
/// the <c>&gt;</c> of <c>=&gt;</c>, <c>&gt;=</c> and <c>-&gt;</c>. Found while closing
/// <c>TOAST-0091</c>: hovering a field name in a short typed literal returned a "Pipeline Data
/// Stream" card about a pipeline that was not there.
/// </remarks>
public sealed class PipelineHoverPrecisionTests
{
    private readonly ToshLanguageFeatures _features = new();

    private string? Hover(string script, int line, int character) =>
        _features.GetHover(script, "test.tosh", new LspPosition(line, character))?.Contents.Value;

    [Theory]
    [InlineData("var r = {| a = 1 |}", 12)]           // inside a record literal
    [InlineData("var x = ($a || $b)", 13)]            // logical or
    [InlineData("var y = [1 <| for i in $z]", 12)]    // comprehension separator
    [InlineData("var c = ($n >= 2)", 13)]             // comparison, not a redirect
    public void A_character_that_is_not_a_pipeline_gets_no_pipeline_hover(string script, int character)
    {
        var hover = Hover(script, 0, character);

        Assert.DoesNotContain("Pipeline Data Stream", hover ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void An_arrow_is_not_a_redirect()
    {
        var hover = Hover("echo (match ($v) {\n    A() => 1\n})", 1, 9);

        Assert.DoesNotContain("Pipeline Data Stream", hover ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void A_real_pipeline_still_gets_its_hover()
    {
        // The control: narrowing the test must not take the feature away.
        var hover = Hover("ls | first", 0, 3);

        Assert.Contains("Pipeline Data Stream", hover ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void A_real_redirect_still_gets_its_hover()
    {
        var hover = Hover("echo hi > out.txt", 0, 8);

        Assert.NotNull(hover);
    }
}
