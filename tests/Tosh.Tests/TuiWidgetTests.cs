using Tosh.Tui;
using Tosh.Tui.Rendering;
using Tosh.Tui.Widgets;

namespace Tosh.Tests;

/// <summary>
/// The widget contract: measure, arrange, draw, and hit testing.
/// </summary>
public sealed class TuiWidgetTests
{
    /// <summary>Renders a widget at a size and returns the grid as text rows.</summary>
    private static string[] Render(TuiWidget widget, int width, int height)
    {
        var buffer = new TuiBuffer(new TuiSize(width, height));
        var bounds = new TuiRect(0, 0, width, height);

        widget.Measure(TuiConstraints.From(new TuiSize(width, height)));
        widget.Arrange(bounds);
        widget.Draw(new TuiSurface(buffer, bounds));

        return Enumerable.Range(0, height).Select(buffer.RowText).ToArray();
    }

    // ── text ────────────────────────────────────────────────────────────────

    [Fact]
    public void Text_measures_the_columns_it_draws_not_the_characters_it_holds()
    {
        var widget = new TuiTextWidget("日本語");

        var size = widget.Measure(TuiConstraints.Unbounded);

        Assert.Equal(6, size.Width);
        Assert.Equal(1, size.Height);
    }

    [Fact]
    public void Text_keeps_the_line_breaks_it_was_given()
    {
        var widget = new TuiTextWidget("CPU 40%\n\nMemory 20%");

        var size = widget.Measure(TuiConstraints.From(new TuiSize(40, 10)));

        Assert.Equal(3, size.Height);
        Assert.Equal(10, size.Width);
    }

    [Fact]
    public void Text_wraps_to_the_width_it_is_given()
    {
        var rows = Render(new TuiTextWidget("one two three four"), width: 9, height: 3);

        Assert.Equal("one two  ", rows[0]);
        Assert.Equal("three    ", rows[1]);
        Assert.Equal("four     ", rows[2]);
    }

    // ── stack ───────────────────────────────────────────────────────────────

    [Fact]
    public void A_fixed_child_gets_exactly_its_size_and_a_star_child_takes_the_rest()
    {
        var stack = new TuiStack(TuiOrientation.Horizontal)
            .Add(new TuiTextWidget("LLLL"), TuiLength.Fixed(4))
            .Add(new TuiTextWidget("RRRRRR"), TuiLength.Star());

        var rows = Render(stack, width: 10, height: 1);

        Assert.Equal("LLLLRRRRRR", rows[0]);
    }

    [Fact]
    public void Star_children_split_what_is_left_by_weight()
    {
        var stack = new TuiStack(TuiOrientation.Horizontal)
            .Add(new TuiTextWidget(new string('a', 20)), TuiLength.Star(1))
            .Add(new TuiTextWidget(new string('b', 20)), TuiLength.Star(3));

        var rows = Render(stack, width: 12, height: 1);

        Assert.Equal("aaabbbbbbbbb", rows[0]);
    }

    [Fact]
    public void An_auto_child_takes_only_what_it_asks_for()
    {
        var stack = new TuiStack(TuiOrientation.Horizontal)
            .Add(new TuiTextWidget("id:"), TuiLength.Auto)
            .Add(new TuiTextWidget(new string('v', 20)), TuiLength.Star());

        var rows = Render(stack, width: 10, height: 1);

        Assert.Equal("id:vvvvvvv", rows[0]);
    }

    [Fact]
    public void A_vertical_stack_places_children_top_to_bottom()
    {
        var stack = new TuiStack(TuiOrientation.Vertical)
            .Add(new TuiTextWidget("top"), TuiLength.Fixed(1))
            .Add(new TuiTextWidget("middle"), TuiLength.Star())
            .Add(new TuiTextWidget("bottom"), TuiLength.Fixed(1));

        var rows = Render(stack, width: 6, height: 3);

        Assert.Equal(["top   ", "middle", "bottom"], rows);
    }

    [Fact]
    public void A_gap_leaves_blank_cells_between_children()
    {
        var stack = new TuiStack(TuiOrientation.Horizontal) { Gap = 2 }
            .Add(new TuiTextWidget("ab"), TuiLength.Fixed(2))
            .Add(new TuiTextWidget("cd"), TuiLength.Fixed(2));

        var rows = Render(stack, width: 6, height: 1);

        Assert.Equal("ab  cd", rows[0]);
    }

    [Fact]
    public void Panes_and_a_status_bar_compose_which_the_old_layouts_could_not()
    {
        // Two panes side by side with a bar underneath both: not expressible as any of
        // single, split-horizontal, split-vertical or stacked.
        var panes = new TuiStack(TuiOrientation.Horizontal)
            .Add(new TuiTextWidget("LL"), TuiLength.Star())
            .Add(new TuiTextWidget("RR"), TuiLength.Star());

        var screen = new TuiStack(TuiOrientation.Vertical)
            .Add(panes, TuiLength.Star())
            .Add(new TuiTextWidget("status"), TuiLength.Fixed(1));

        var rows = Render(screen, width: 6, height: 2);

        Assert.Equal(["LL RR ", "status"], rows);
    }

    // ── border ──────────────────────────────────────────────────────────────

    [Fact]
    public void A_border_draws_a_box_around_its_child()
    {
        var rows = Render(new TuiBorder(new TuiTextWidget("hi")), width: 6, height: 3);

        Assert.Equal(["╭────╮", "│hi  │", "╰────╯"], rows);
    }

    [Fact]
    public void A_border_puts_its_title_in_the_top_edge()
    {
        var rows = Render(new TuiBorder(new TuiTextWidget(""), "Files"), width: 12, height: 3);

        // Twelve columns: two corners, a padded seven-column title, three dashes.
        Assert.Equal("╭ Files ───╮", rows[0]);
    }

    [Fact]
    public void A_border_truncates_a_title_that_does_not_fit()
    {
        var rows = Render(new TuiBorder(null, "A very long title"), width: 10, height: 3);

        Assert.StartsWith("╭ A very", rows[0], StringComparison.Ordinal);
        Assert.EndsWith("╮", rows[0], StringComparison.Ordinal);
    }

    [Fact]
    public void A_border_asks_for_two_cells_more_than_its_child()
    {
        var border = new TuiBorder(new TuiTextWidget("abc"));

        var size = border.Measure(TuiConstraints.Unbounded);

        Assert.Equal(5, size.Width);
        Assert.Equal(3, size.Height);
    }

    [Fact]
    public void A_border_with_no_room_inside_draws_nothing_rather_than_failing()
    {
        var rows = Render(new TuiBorder(new TuiTextWidget("hidden")), width: 2, height: 2);

        Assert.Equal(["╭╮", "╰╯"], rows);
    }

    // ── hit testing ─────────────────────────────────────────────────────────

    [Fact]
    public void Hit_testing_finds_the_deepest_widget_at_a_point()
    {
        var left = new TuiTextWidget("L");
        var right = new TuiTextWidget("R");
        var stack = new TuiStack(TuiOrientation.Horizontal)
            .Add(left, TuiLength.Star())
            .Add(right, TuiLength.Star());

        stack.Arrange(new TuiRect(0, 0, 10, 1));

        Assert.Same(left, stack.HitTest(2, 0));
        Assert.Same(right, stack.HitTest(7, 0));
    }

    [Fact]
    public void Hit_testing_outside_everything_finds_nothing()
    {
        var stack = new TuiStack().Add(new TuiTextWidget("x"), TuiLength.Fixed(1));
        stack.Arrange(new TuiRect(0, 0, 4, 1));

        Assert.Null(stack.HitTest(9, 9));
    }

    [Fact]
    public void Hit_testing_through_a_border_finds_the_child_inside_it()
    {
        var inner = new TuiTextWidget("inner");
        var border = new TuiBorder(inner);
        border.Arrange(new TuiRect(0, 0, 10, 3));

        Assert.Same(inner, border.HitTest(3, 1));

        // The frame itself is the border, not the child.
        Assert.Same(border, border.HitTest(0, 0));
    }

    // ── input ───────────────────────────────────────────────────────────────

    [Fact]
    public void A_container_does_not_hand_input_to_its_children()
    {
        // Containers used to offer every event to every child in turn, which let an
        // unfocused widget consume something meant for the focused one. Delivery is
        // TuiFocus's job now; a container answers only for itself.
        var child = new ConsumingWidget(consume: true);
        var stack = new TuiStack().Add(child, TuiLength.Fixed(1));

        var handled = stack.OnInput(TuiInputEvent.FromKey(new ConsoleKeyInfo('q', ConsoleKey.Q, false, false, false)));

        Assert.False(handled);
        Assert.False(child.Saw);
    }

    [Fact]
    public void A_fixed_size_settles_one_dimension_not_both()
    {
        // A row containing anything fixed used to report the full offered height, because
        // a fixed length was read as the whole size rather than as one of its two numbers.
        // A labelled field is such a row, so a column of them gave everything to the first.
        var rows = Render(
            new TuiStack(TuiOrientation.Vertical)
                .Add(new TuiStack(TuiOrientation.Horizontal)
                    .Add(new TuiTextWidget("a:"), TuiLength.Fixed(3))
                    .Add(new TuiTextWidget("one"), TuiLength.Star()))
                .Add(new TuiStack(TuiOrientation.Horizontal)
                    .Add(new TuiTextWidget("b:"), TuiLength.Fixed(3))
                    .Add(new TuiTextWidget("two"), TuiLength.Star())),
            width: 8,
            height: 4);

        Assert.Equal(["a: one  ", "b: two  ", "        ", "        "], rows);
    }

    [Fact]
    public void A_labelled_field_is_one_row_tall_however_much_room_it_is_offered()
    {
        var field = new TuiField("Capacity", "25000");

        Assert.Equal(1, field.Measure(TuiConstraints.From(new TuiSize(40, 24))).Height);
    }

    private sealed class ConsumingWidget(bool consume) : TuiWidget
    {
        public bool Saw { get; private set; }

        public override TuiSize Measure(TuiConstraints constraints) => new(1, 1);

        public override void Draw(TuiSurface surface)
        {
        }

        public override bool OnInput(TuiInputEvent input)
        {
            Saw = true;
            return consume;
        }
    }
}
