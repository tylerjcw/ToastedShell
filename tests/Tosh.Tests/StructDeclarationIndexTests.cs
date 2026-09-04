using Tosh.LanguageServices;

namespace Tosh.Tests;

/// <summary>
/// Structs are visible to the editor — <c>TOAST-0114</c>.
/// </summary>
/// <remarks>
/// <para>
/// Measured before the fix: a struct produced no outline entry, no hover, no completion and no
/// go-to-definition. <c>DeclarationIndex</c> had no case for
/// <c>StructDefinitionStatementSyntax</c> at all, so a struct simply was not there — the cursor
/// inside <c>new Vec2 {| … |}</c> returned the same 372 generic entries it returns anywhere.
/// </para>
/// <para>
/// A struct is the one declaration carrying <em>both</em> shapes: record-style fields from
/// <c>struct Pair(A: int, B: int)</c> and class-style members from a braced body. Both are
/// indexed, and the initializable-member lookup is the one place that has to ask for two member
/// kinds rather than one.
/// </para>
/// </remarks>
public sealed class StructDeclarationIndexTests
{
    private readonly ToshLanguageFeatures _features = new();

    private const string Source = """
        struct Vec2 {
            prop X = 0
            prop Y = 0
        }
        struct Pair(A: int, B: int)
        var v = new Vec2 {|  |}
        var p = new Pair {|  |}
        """;

    private IReadOnlyList<string> Labels(int line, int character) =>
        _features.GetCompletionItems(Source, new LspPosition(line, character))
            .Select(item => item.Label)
            .ToArray();

    [Fact]
    public void A_braced_struct_offers_its_properties()
    {
        Assert.Equal(new[] { "X", "Y" }, Labels(5, 20));
    }

    [Fact]
    public void A_parenthesised_struct_offers_its_fields()
    {
        Assert.Equal(new[] { "A", "B" }, Labels(6, 20));
    }

    [Fact]
    public void A_struct_appears_in_the_document_outline()
    {
        var names = _features.GetDocumentSymbols(Source, "test.tosh").Select(symbol => symbol.Name).ToArray();

        Assert.Contains("Vec2", names);
        Assert.Contains("Pair", names);
    }

    [Fact]
    public void Hover_on_a_struct_names_it_as_one()
    {
        var hover = _features.GetHover(Source, "test.tosh", new LspPosition(0, 8));

        Assert.NotNull(hover);
        Assert.Contains("Vec2", hover!.Contents.Value, StringComparison.Ordinal);
        Assert.Contains("Struct", hover.Contents.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void A_struct_is_offered_as_a_type_name()
    {
        // It is a type-like symbol, so it belongs in the completion list where types go.
        var labels = Labels(6, 20);

        Assert.DoesNotContain("Vec2", labels);   // not inside a Pair literal
        Assert.Contains("Pair", _features.GetCompletionItems(Source + "\nvar q: Pa", new LspPosition(7, 9))
            .Select(item => item.Label));
    }

    [Fact]
    public void A_class_and_a_record_are_unaffected()
    {
        // The control: adding a third shape must not disturb the two that worked.
        const string mixed = """
            class Box { prop Name = "" }
            record Point(X: int, Y: int)
            var b = new Box {|  |}
            var p = new Point {|  |}
            """;

        Assert.Equal(
            new[] { "Name" },
            _features.GetCompletionItems(mixed, new LspPosition(2, 19)).Select(i => i.Label).ToArray());

        Assert.Equal(
            new[] { "X", "Y" },
            _features.GetCompletionItems(mixed, new LspPosition(3, 21)).Select(i => i.Label).ToArray());
    }
}
