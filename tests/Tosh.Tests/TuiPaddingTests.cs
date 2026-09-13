using System.Dynamic;
using Tosh.Tui;
using Tosh.Tui.Declarative;
using Tosh.Tui.Rendering;
using Tosh.Tui.Widgets;

namespace Tosh.Tests;

/// <summary>
/// Padding and alignment, which every widget carries rather than being wrapped in.
/// </summary>
/// <remarks>
/// Lipgloss is the argument: treating these as properties of a renderable rather than as
/// containers is why its layouts read as short as they do (<c>TUI-0021</c>).
/// </remarks>
public sealed class TuiPaddingTests
{
    private static string[] Render(TuiWidget widget, int width, int height)
    {
        var buffer = new TuiBuffer(new TuiSize(width, height));
        var bounds = new TuiRect(0, 0, width, height);

        widget.Measure(TuiConstraints.From(new TuiSize(width, height)));
        widget.Arrange(bounds);
        widget.Paint(new TuiSurface(buffer, bounds));

        return [.. Enumerable.Range(0, height).Select(row => buffer.RowText(row).TrimEnd())];
    }

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

    [Theory]
    [InlineData("1", 1, 1, 1, 1)]
    [InlineData("1 2", 2, 1, 2, 1)]
    [InlineData("1 2 3 4", 4, 1, 2, 3)]
    [InlineData("nonsense", 0, 0, 0, 0)]
    public void A_thickness_is_written_the_way_css_writes_one(
        string text, int left, int top, int right, int bottom)
    {
        var padding = TuiThickness.Parse(text);

        Assert.Equal(left, padding.Left);
        Assert.Equal(top, padding.Top);
        Assert.Equal(right, padding.Right);
        Assert.Equal(bottom, padding.Bottom);
    }

    [Fact]
    public void Padding_insets_what_a_widget_draws()
    {
        var text = new TuiTextWidget("hi") { Padding = 1, Wrap = false };

        Assert.Equal(["", " hi", ""], Render(text, 6, 3));
    }

    [Fact]
    public void Padding_is_counted_in_the_measure_or_the_content_is_clipped_not_inset()
    {
        // The bug this always ships with the first time: measure forgets the padding, the
        // parent hands over exactly the content's size, and the inset eats the content.
        var bare = new TuiTextWidget("hi") { Wrap = false };
        var padded = new TuiTextWidget("hi") { Padding = "1 2", Wrap = false };

        var room = TuiConstraints.Unbounded;

        Assert.Equal(new TuiSize(2, 1), bare.Measure(room));
        Assert.Equal(new TuiSize(6, 3), padded.Measure(room));
    }

    [Fact]
    public void Padding_works_inside_a_container_without_the_container_knowing()
    {
        var stack = new TuiStack(TuiOrientation.Vertical)
            .Add(new TuiTextWidget("a") { Wrap = false }, TuiLength.Fixed(1))
            .Add(new TuiTextWidget("b") { Wrap = false, Padding = "0 2" }, TuiLength.Fixed(1));

        Assert.Equal(["a", "  b"], Render(stack, 8, 2));
    }

    [Fact]
    public void A_widget_can_sit_anywhere_across_its_slot()
    {
        static string Row(TuiHorizontalAlignment align)
            => Render(new TuiTextWidget("ab") { Wrap = false, Align = align }, 8, 1)[0];

        Assert.Equal("ab", Row(TuiHorizontalAlignment.Left));
        Assert.Equal("   ab", Row(TuiHorizontalAlignment.Center));
        Assert.Equal("      ab", Row(TuiHorizontalAlignment.Right));
    }

    [Fact]
    public void A_widget_can_sit_anywhere_down_its_slot()
    {
        static string[] Rows(TuiVerticalAlignment align)
            => Render(new TuiTextWidget("x") { Wrap = false, VerticalAlign = align }, 4, 3);

        Assert.Equal(["x", "", ""], Rows(TuiVerticalAlignment.Top));
        Assert.Equal(["", "x", ""], Rows(TuiVerticalAlignment.Middle));
        Assert.Equal(["", "", "x"], Rows(TuiVerticalAlignment.Bottom));
    }

    [Fact]
    public void Stretch_is_the_default_because_almost_everything_takes_what_it_is_offered()
    {
        var widget = new TuiTextWidget("x");

        Assert.Equal(TuiHorizontalAlignment.Stretch, widget.Align);
        Assert.Equal(TuiVerticalAlignment.Stretch, widget.VerticalAlign);

        widget.Arrange(new TuiRect(0, 0, 10, 4));

        Assert.Equal(new TuiRect(0, 0, 10, 4), widget.Bounds);
    }

    [Fact]
    public void Alignment_happens_inside_the_padding_rather_than_being_pushed_off_by_it()
    {
        var text = new TuiTextWidget("ab")
        {
            Wrap = false,
            Padding = "0 1",
            Align = TuiHorizontalAlignment.Center,
        };

        // Ten columns, one off each side, so "ab" is centred in the eight that remain.
        Assert.Equal(["    ab"], Render(text, 10, 1));
    }

    [Fact]
    public void A_widget_knows_both_the_slot_it_was_given_and_where_it_draws()
    {
        var text = new TuiTextWidget("x") { Padding = 2 };

        text.Arrange(new TuiRect(0, 0, 10, 6));

        Assert.Equal(new TuiRect(0, 0, 10, 6), text.Slot);
        Assert.Equal(new TuiRect(2, 2, 6, 2), text.Bounds);
    }

    [Fact]
    public void Markup_carries_them_on_every_widget()
    {
        var widget = TuiTreeBuilder.Build(
            Node(("Text", "hi"), ("Padding", "1 2"), ("Align", "center"), ("VAlign", "bottom")));

        Assert.Equal(new TuiThickness(2, 1, 2, 1), widget.Padding);
        Assert.Equal(TuiHorizontalAlignment.Center, widget.Align);
        Assert.Equal(TuiVerticalAlignment.Bottom, widget.VerticalAlign);
    }

    [Fact]
    public void A_padded_box_keeps_its_border_and_insets_only_its_contents()
    {
        var box = new TuiBorder(new TuiTextWidget("x") { Wrap = false, Padding = 1 }, "T");

        Assert.Equal(
            // One column for the border, one for the padding.
            ["╭ T ────╮", "│       │", "│ x     │", "│       │", "╰───────╯"],
            Render(box, 9, 5));
    }
}
