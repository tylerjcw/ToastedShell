using Tosh.LanguageServices;

namespace Tosh.Tests;

/// <summary>
/// Completion after the path operator — <c>TOAST-0090</c>.
/// </summary>
/// <remarks>
/// <para>
/// Measured before the fix, <c>Level::</c> and <c>Level.</c> returned byte-identical lists of 373
/// generic entries, and <c>Novice</c> was in neither: the qualified-context scan split on '.'
/// only, so a path found no context at all, and even when a context was found nothing answered
/// for a declared enum, so the caller fell through to the global list.
/// </para>
/// <para>
/// The two are answered distinctly rather than identically. A path reaches inside a type, so a
/// value's instance members are not offered through it.
/// </para>
/// </remarks>
public sealed class PathCompletionTests
{
    private readonly ToshLanguageFeatures _features = new();

    private const string EnumScript = """
        enum Level: int { Novice = 0, Apprentice = 1, Expert = 2 }
        var a = Level::
        """;

    private const string UnionScript = """
        union Shape { Circle(r), Square(side), Empty }
        var s = Shape::
        """;

    private IReadOnlyList<string> Labels(string script, int line, int character) =>
        _features.GetCompletionItems(script, new LspPosition(line, character))
            .Select(item => item.Label)
            .ToArray();

    [Fact]
    public void A_path_offers_the_enum_members()
    {
        var labels = Labels(EnumScript, 1, 15);

        Assert.Equal(new[] { "Apprentice", "Expert", "Novice" }, labels.OrderBy(l => l, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void A_path_offers_the_union_variants()
    {
        var labels = Labels(UnionScript, 1, 15);

        Assert.Equal(new[] { "Circle", "Empty", "Square" }, labels.OrderBy(l => l, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void A_partial_after_the_path_narrows_it()
    {
        var labels = Labels("enum Level: int { Novice = 0, Expert = 1 }\nvar a = Level::No", 1, 17);

        Assert.Equal(new[] { "Novice" }, labels);
    }

    [Fact]
    public void A_member_access_still_offers_them_because_that_spelling_still_works()
    {
        // `TOAST-0090` kept `Type.Member` working, so it has to keep completing.
        var labels = Labels("enum Level: int { Novice = 0, Expert = 1 }\nvar a = Level.", 1, 14);

        Assert.Contains("Novice", labels);
        Assert.Contains("Expert", labels);
    }

    [Fact]
    public void The_member_is_labelled_with_the_type_it_came_from()
    {
        var items = _features.GetCompletionItems(EnumScript, new LspPosition(1, 15));

        var novice = Assert.Single(items, item => item.Label == "Novice");
        Assert.Equal("Enum member of Level", novice.Detail);
    }

    [Fact]
    public void A_variant_is_labelled_as_one()
    {
        var items = _features.GetCompletionItems(UnionScript, new LspPosition(1, 15));

        var circle = Assert.Single(items, item => item.Label == "Circle");
        Assert.Equal("Variant of Shape", circle.Detail);
    }

    [Fact]
    public void A_path_after_an_unknown_type_offers_nothing_of_its_own()
    {
        // It must not silently fall through to the global list, which is what made the bug
        // invisible: 373 entries look like a working completion until you look for the member.
        var labels = Labels("var a = NoSuchType::", 0, 20);

        Assert.DoesNotContain("Novice", labels);
        Assert.DoesNotContain("echo", labels);
    }

    [Fact]
    public void A_type_annotation_is_not_mistaken_for_a_path()
    {
        // The reason ':' is not simply a path character: the scan would run back through the
        // annotation colon and complete against `a:Level`.
        var labels = Labels("enum Level: int { Novice = 0 }\nvar a:Level.", 1, 12);

        Assert.Contains("Novice", labels);
    }

    [Fact]
    public void A_path_does_not_offer_instance_members_of_a_value()
    {
        const string script = """
            class Box { prop Name = "" }
            var b = new Box()
            echo $b::
            """;

        var labels = Labels(script, 2, 9);

        Assert.DoesNotContain("Name", labels);
    }

    [Fact]
    public void Hover_on_a_path_member_names_the_type_it_belongs_to()
    {
        // Measured before the fix: hover on `Level` returned "**Level** / Enum" and hover on
        // `Novice` returned null.
        var hover = _features.GetHover(EnumScript + "Novice", "test.tosh", new LspPosition(1, 17));

        Assert.NotNull(hover);
        Assert.Contains("Enum member of `Level`", hover!.Contents.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void Hover_on_a_variant_names_its_union()
    {
        var hover = _features.GetHover(UnionScript + "Circle", "test.tosh", new LspPosition(1, 17));

        Assert.NotNull(hover);
        Assert.Contains("Variant of `Shape`", hover!.Contents.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void Hover_on_the_type_itself_is_unchanged()
    {
        var hover = _features.GetHover(EnumScript + "Novice", "test.tosh", new LspPosition(1, 10));

        Assert.NotNull(hover);
        Assert.Contains("Enum", hover!.Contents.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void A_member_access_on_a_value_still_offers_instance_members()
    {
        const string script = """
            class Box { prop Name = "" }
            var b = new Box()
            echo $b.
            """;

        var labels = Labels(script, 2, 8);

        Assert.Contains("Name", labels);
    }
}
