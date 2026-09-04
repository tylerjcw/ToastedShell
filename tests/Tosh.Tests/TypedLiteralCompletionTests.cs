using Tosh.LanguageServices;

namespace Tosh.Tests;

/// <summary>
/// Completion and hover inside <c>new T {| … |}</c> — <c>TOAST-0091</c>.
/// </summary>
/// <remarks>
/// <para>
/// Measured before the fix, the cursor inside a typed literal returned the same 373 generic
/// entries as anywhere else, with the type's own members in none of them: a field name is a bare
/// identifier, so nothing in the completion chain recognised the position and the caller fell
/// through to the global list.
/// </para>
/// <para>
/// The literal is found by scanning <em>forward</em> from the start of the document rather than
/// backward from the cursor. Backward is cheaper and wrong — a <c>"{|"</c> inside a string or a
/// <c>#</c> comment would open a literal that is not there — and both of those have tests.
/// </para>
/// </remarks>
public sealed class TypedLiteralCompletionTests
{
    private readonly ToshLanguageFeatures _features = new();

    private const string Types = """
        class Box {
            prop Name = ""
            prop Size = 0
        }
        record Point(X: int, Y: int)
        """;

    private IReadOnlyList<string> Labels(string script, int line, int character) =>
        _features.GetCompletionItems(script, new LspPosition(line, character))
            .Select(item => item.Label)
            .ToArray();

    [Fact]
    public void A_class_literal_offers_its_properties()
    {
        var labels = Labels(Types + "\nvar b = new Box {|  |}", 5, 19);

        Assert.Equal(new[] { "Name", "Size" }, labels);
    }

    [Fact]
    public void A_record_literal_offers_its_fields()
    {
        var labels = Labels(Types + "\nvar p = new Point {|  |}", 5, 21);

        Assert.Equal(new[] { "X", "Y" }, labels);
    }

    [Fact]
    public void A_partial_field_name_narrows_it()
    {
        var labels = Labels(Types + "\nvar b = new Box {| Na", 5, 21);

        Assert.Equal(new[] { "Name" }, labels);
    }

    [Fact]
    public void The_form_with_constructor_arguments_is_recognised()
    {
        var labels = Labels(Types + "\nvar b = new Box(1) {|  |}", 5, 22);

        Assert.Equal(new[] { "Name", "Size" }, labels);
    }

    [Fact]
    public void A_field_is_offered_with_its_assignment()
    {
        var items = _features.GetCompletionItems(
            Types + "\nvar b = new Box {|  |}", new LspPosition(5, 19));

        var name = Assert.Single(items, item => item.Label == "Name");
        Assert.Equal("Name = ", name.InsertText);
        Assert.Equal("Property of Box", name.Detail);
    }

    [Fact]
    public void A_value_position_is_not_a_field_position()
    {
        // After `=` the author is writing a value, and the type's member names are not what
        // belongs there — so the ordinary sources answer instead.
        var labels = Labels(Types + "\nvar b = new Box {| Name = ", 5, 26);

        Assert.DoesNotContain("Size", labels);
        Assert.Contains("echo", labels);
    }

    [Fact]
    public void An_untyped_record_literal_is_left_alone()
    {
        // `{| a = 1 |}` has no type to complete against, and must not acquire one.
        var labels = Labels(Types + "\nvar r = {|  |}", 5, 11);

        Assert.DoesNotContain("Name", labels);
        Assert.Contains("echo", labels);
    }

    [Fact]
    public void A_brace_pipe_inside_a_string_does_not_open_a_literal()
    {
        // The reason the scan runs forward from the start of the document.
        var labels = Labels(Types + "\nvar s = \"{|\"\nvar t = ", 6, 8);

        Assert.DoesNotContain("Name", labels);
    }

    [Fact]
    public void A_brace_pipe_inside_a_comment_does_not_open_a_literal()
    {
        var labels = Labels(Types + "\n# new Box {|\nvar t = ", 6, 8);

        Assert.DoesNotContain("Name", labels);
    }

    [Fact]
    public void A_nested_literal_completes_against_the_inner_type()
    {
        const string nested = """
            class Inner { prop Deep = 0 }
            class Outer { prop Child = 0 }
            var o = new Outer {| Child = new Inner {|  |} |}
            """;

        var labels = Labels(nested, 2, 44);

        Assert.Equal(new[] { "Deep" }, labels);
    }

    [Fact]
    public void Hover_on_a_field_names_the_type_it_belongs_to()
    {
        var hover = _features.GetHover(
            Types + "\nvar b = new Box {| Name = \"x\" |}", "test.tosh", new LspPosition(5, 21));

        Assert.NotNull(hover);
        Assert.Contains("Property of `Box`", hover!.Contents.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void Hover_on_a_record_field_calls_it_a_field()
    {
        var hover = _features.GetHover(
            Types + "\nvar p = new Point {| X = 1 |}", "test.tosh", new LspPosition(5, 21));

        Assert.NotNull(hover);
        Assert.Contains("Field of `Point`", hover!.Contents.Value, StringComparison.Ordinal);
    }
}
