using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// A documented declaration says what it is for when you ask `help`.
///
/// `TS-P2-101`. A `##` block parsed into the declaration syntax and was indexed
/// for LSP hover, but no runtime definition carried it, so `help Color` on a
/// fully documented class answered only the synthesised `ToSh type Color.` — the
/// name and kind, which the reader could already see. A library documented to the
/// house standard was discoverable in the editor and invisible in the shell.
///
/// The synthesised line is still the fallback, and that matters: an undocumented
/// type must keep saying something rather than nothing.
/// </summary>
public class DeclarationDocumentationTests
{
    /// <summary>
    /// The topic's description, read from the value `help` yields rather than from
    /// captured output.
    /// </summary>
    /// <remarks>
    /// `help` produces a topic object and the CLI's display engine renders it, so
    /// capturing stdout measures the renderer rather than the topic — and it does
    /// so inconsistently: a class printed while a struct produced nothing at all,
    /// which read as a missing fix rather than a mis-aimed probe.
    /// </remarks>
    private static async Task<string> DescriptionAsync(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);
        var results = await engine.ExecuteToListAsync(source);
        return Assert.Single(results)?.ToString() ?? string.Empty;
    }

    /// <summary>
    /// Every declaration kind that has a runtime definition. Each carried the
    /// comment as far as the parser and dropped it at the same place, so fixing
    /// one without the others would have left the surface inconsistent in a way a
    /// single-kind test could not see.
    /// </summary>
    [Theory]
    [InlineData("## @summary A documented class.\nclass T { prop P = 1 }", "A documented class.")]
    [InlineData("## @summary A documented record.\nrecord T(a: int)", "A documented record.")]
    [InlineData("## @summary A documented struct.\nstruct T { prop F = 1 }", "A documented struct.")]
    [InlineData("## @summary A documented enum.\nenum T: int { One = 1 }", "A documented enum.")]
    [InlineData("## @summary A documented interface.\ninterface T { func Go() -> int }", "A documented interface.")]
    public async Task Help_returns_the_declarations_own_documentation(string declaration, string expected)
        => Assert.Contains(expected, await DescriptionAsync(declaration + "\n(help T).Description"));

    /// <summary>
    /// The fallback. Removing the synthesised description in favour of the doc
    /// comment would leave an undocumented type with an empty help entry, which is
    /// worse than a generic one.
    /// </summary>
    [Fact]
    public async Task An_undocumented_declaration_still_describes_itself()
        => Assert.Contains("ToSh type T", await DescriptionAsync("class T { prop P = 1 }\n(help T).Description"));

    /// <summary>
    /// Only the summary is promoted, not the whole comment: `help` shows a
    /// description, and a multi-tag block would otherwise arrive as one run-on
    /// paragraph.
    /// </summary>
    [Fact]
    public async Task Only_the_summary_becomes_the_description()
    {
        var text = await DescriptionAsync(
            """
            ## @summary The short line.
            ## @remarks A longer explanation that belongs elsewhere.
            class T { prop P = 1 }
            (help T).Description
            """);

        Assert.Contains("The short line.", text);
        Assert.DoesNotContain("A longer explanation", text);
    }

    /// <summary>
    /// Functions already worked, and are asserted here so a change to the shared
    /// help path cannot quietly regress the case that was the reference for what
    /// the others should do.
    /// </summary>
    [Fact]
    public async Task A_documented_function_still_reports_its_documentation()
        => Assert.Contains("Adds two numbers.", await DescriptionAsync(
            """
            ## @summary Adds two numbers.
            func add(a: int, b: int) -> int => ($a + $b)
            (help add).Description
            """));
}
