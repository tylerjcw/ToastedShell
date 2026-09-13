using System.Dynamic;
using Tosh.Tui;
using Tosh.Tui.Declarative;
using Tosh.Tui.Rendering;
using Tosh.Tui.Widgets;

namespace Tosh.Tests;

/// <summary>
/// Record trees into widget trees — the "markup" half of the declarative surface.
/// </summary>
public sealed class TuiTreeBuilderTests
{
    /// <summary>A record literal, as the language produces one.</summary>
    private static IDictionary<string, object?> Node(params (string Key, object? Value)[] fields)
    {
        var record = new ExpandoObject();
        var dictionary = (IDictionary<string, object?>)record;

        foreach (var (key, value) in fields)
        {
            dictionary[key] = value;
        }

        return dictionary;
    }

    private static string[] Render(TuiWidget widget, int width, int height)
    {
        var buffer = new TuiBuffer(new TuiSize(width, height));
        var bounds = new TuiRect(0, 0, width, height);

        widget.Measure(TuiConstraints.From(new TuiSize(width, height)));
        widget.Arrange(bounds);
        widget.Draw(new TuiSurface(buffer, bounds));

        return [.. Enumerable.Range(0, height).Select(row => buffer.RowText(row).TrimEnd())];
    }

    [Fact]
    public void The_key_that_names_a_widget_carries_its_main_argument()
    {
        var widget = TuiTreeBuilder.Build(Node(("Text", "hello")));

        Assert.IsType<TuiTextWidget>(widget);
        Assert.Equal(["hello"], Render(widget, 10, 1));
    }

    [Fact]
    public void Other_keys_are_properties_of_that_widget()
    {
        var widget = (TuiTextWidget)TuiTreeBuilder.Build(Node(("Text", "hi"), ("Bold", true), ("Foreground", "red")));

        Assert.Equal(TuiTextAttributes.Bold, widget.Style.Attributes);
        Assert.Equal("red", widget.Style.Foreground);
    }

    [Fact]
    public void An_id_is_kept_so_a_result_can_be_keyed_by_it()
    {
        var widget = TuiTreeBuilder.Build(Node(("Field", "Width"), ("Id", "Width"), ("Value", "2")));

        // The id is on the row, which answers with what its input holds — so a caller
        // names the thing they wrote rather than a widget synthesised inside it.
        var field = Assert.IsType<TuiField>(widget);

        Assert.Equal("Width", field.Id);
        Assert.Equal("2", field.Text);
        Assert.Equal("2", field.Value);
    }

    [Fact]
    public void A_column_nests_its_children()
    {
        var widget = TuiTreeBuilder.Build(Node(("Column", new object?[]
        {
            Node(("Text", "top")),
            Node(("Text", "bottom")),
        })));

        Assert.Equal(["top", "bottom"], Render(widget, 10, 2));
    }

    [Fact]
    public void A_row_places_its_children_side_by_side()
    {
        var widget = TuiTreeBuilder.Build(Node(("Row", new object?[]
        {
            Node(("Text", "LL"), ("Size", 2)),
            Node(("Text", "RR"), ("Size", "*")),
        })));

        Assert.Equal(["LLRR"], Render(widget, 4, 1));
    }

    [Fact]
    public void A_size_may_be_cells_a_star_or_auto()
    {
        var widget = TuiTreeBuilder.Build(Node(("Row", new object?[]
        {
            Node(("Text", "ab"), ("Size", "auto")),
            Node(("Text", "cdefgh"), ("Size", "2*")),
            Node(("Text", "ij"), ("Size", 2)),
        })));

        Assert.Equal(["abcdefij"], Render(widget, 8, 1));
    }

    [Fact]
    public void A_bare_value_among_children_is_text()
    {
        var widget = TuiTreeBuilder.Build(Node(("Column", new object?[] { "one", "two" })));

        Assert.Equal(["one", "two"], Render(widget, 5, 2));
    }

    [Fact]
    public void A_box_draws_a_border_round_what_it_holds()
    {
        var widget = TuiTreeBuilder.Build(
            Node(("Box", new object?[] { Node(("Text", "in")) }), ("Title", "T")));

        Assert.Equal(["╭ T ──╮", "│in   │", "╰─────╯"], Render(widget, 7, 3));
    }

    [Fact]
    public void A_list_needs_no_wrapper_to_scroll()
    {
        // A list scrolls itself. Wrapping one was the ritual this is meant to remove, and
        // the wrapper caused more trouble than it saved — see TuiList.Offset.
        var widget = TuiTreeBuilder.Build(Node(("List", new object?[] { "a", "b", "c" })));

        Assert.IsType<TuiList>(widget);
    }

    [Fact]
    public void A_widget_built_in_code_can_sit_in_a_record_tree()
    {
        var made = new TuiTextWidget("made in code");

        var widget = TuiTreeBuilder.Build(Node(("Column", new object?[] { made, Node(("Text", "declared")) })));

        Assert.Equal(["made in code", "declared"], Render(widget, 12, 2));
    }

    [Fact]
    public void A_node_naming_nothing_says_what_it_could_have_named()
    {
        var error = Assert.Throws<ArgumentException>(
            () => TuiTreeBuilder.Build(Node(("Nonsense", "x"))));

        Assert.Contains("No widget was named", error.Message, StringComparison.Ordinal);
        Assert.Contains("list", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_registry_is_open_so_a_new_widget_can_be_added()
    {
        var registry = TuiWidgetRegistry.CreateDefault()
            .Register("banner", static (spec, _) => new TuiTextWidget($"** {spec.Primary} **"));

        var widget = TuiTreeBuilder.Build(Node(("Banner", "hi")), registry);

        Assert.Equal(["** hi **"], Render(widget, 10, 1));
    }

    private static IEnumerable<TuiWidget> Descendants(TuiWidget widget)
    {
        yield return widget;

        foreach (var child in widget.Children)
        {
            foreach (var nested in Descendants(child))
            {
                yield return nested;
            }
        }
    }
}
