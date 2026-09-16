using Tosh.Tui;
using Tosh.Tui.Declarative;
using Tosh.Tui.Rendering;
using Tosh.Tui.Widgets;

namespace Tosh.Tests;

/// <summary>
/// One value chosen from a list that opens out of it (<c>TUI-0023</c>).
/// </summary>
public sealed class TuiComboTests
{
    private static TuiInputEvent Named(ConsoleKey key)
        => TuiInputEvent.FromKey(new ConsoleKeyInfo('\0', key, false, false, false));

    private static string[] Render(TuiWidget widget, int width, int height)
    {
        var buffer = new TuiBuffer(new TuiSize(width, height));
        var bounds = new TuiRect(0, 0, width, height);

        widget.Measure(TuiConstraints.From(new TuiSize(width, height)));
        widget.Arrange(bounds);
        widget.Paint(new TuiSurface(buffer, bounds));

        return [.. Enumerable.Range(0, height).Select(row => buffer.RowText(row).TrimEnd())];
    }

    private static TuiCombo Sample() => new(["Water", "Sodium", "Helium"]);

    [Fact]
    public void It_shows_the_chosen_value_and_a_mark_that_says_it_opens()
    {
        var combo = Sample();

        Assert.Equal(["  Water              ▾"], Render(combo, 22, 1));
    }

    [Fact]
    public void It_is_as_wide_as_its_widest_option()
    {
        // So a column of them lines up and the field does not change width as the choice
        // does, which is what makes a form of them read as a form.
        var combo = Sample();

        Assert.Equal(TuiTextMeasure.MeasureWidth("Sodium") + 4, combo.Measure(TuiConstraints.Unbounded).Width);
    }

    [Fact]
    public void Down_opens_it_on_what_is_already_chosen()
    {
        var combo = Sample();

        Assert.Null(combo.Popup);

        combo.Selected = 2;
        combo.OnInput(Named(ConsoleKey.DownArrow));

        Assert.NotNull(combo.Popup);
        Assert.Same(combo, combo.PopupAnchor);
    }

    [Fact]
    public void Left_and_right_step_through_without_opening_it()
    {
        // What a reader who already knows the options wants, and what every other combo
        // box does.
        var chosen = new List<object?>();
        var combo = Sample();

        combo.Changed = chosen.Add;

        combo.OnInput(Named(ConsoleKey.RightArrow));

        Assert.Equal("Sodium", combo.SelectedItem);
        Assert.Null(combo.Popup);

        combo.OnInput(Named(ConsoleKey.LeftArrow));

        Assert.Equal("Water", combo.SelectedItem);
        Assert.Equal(["Sodium", "Water"], chosen);
    }

    [Fact]
    public void Stepping_past_the_end_stays_put_and_says_nothing()
    {
        var chosen = 0;
        var combo = Sample();

        combo.Changed = _ => chosen += 1;
        combo.OnInput(Named(ConsoleKey.LeftArrow));

        Assert.Equal("Water", combo.SelectedItem);
        Assert.Equal(0, chosen);
    }

    [Fact]
    public void Choosing_from_the_list_closes_it_and_says_which()
    {
        object? chosen = null;
        var combo = Sample();
        var page = new TuiTextField("field");
        var root = new TuiStack(TuiOrientation.Vertical) { Items = [combo, page] };

        combo.Changed = item => chosen = item;

        using var screen = new TuiDeclarativeScreen(root, []);

        screen.Render(new TuiSize(30, 8));
        combo.OnInput(Named(ConsoleKey.DownArrow));
        screen.Render(new TuiSize(30, 8));

        // The list has the keyboard now, because the list is the modal on the layer — so
        // moving and choosing are the menu's rather than written twice.
        screen.HandleInput(Named(ConsoleKey.DownArrow));
        screen.HandleInput(Named(ConsoleKey.Enter));

        Assert.Equal("Sodium", chosen);
        Assert.Equal("Sodium", combo.SelectedItem);
        Assert.Null(combo.Popup);
    }

    [Fact]
    public void Escape_closes_it_without_choosing()
    {
        var chosen = 0;
        var combo = Sample();
        var root = new TuiStack(TuiOrientation.Vertical) { Items = [combo, new TuiTextField("field")] };

        combo.Changed = _ => chosen += 1;

        using var screen = new TuiDeclarativeScreen(root, []);

        screen.Render(new TuiSize(30, 8));
        combo.OnInput(Named(ConsoleKey.DownArrow));
        screen.Render(new TuiSize(30, 8));

        screen.HandleInput(Named(ConsoleKey.DownArrow));
        screen.HandleInput(Named(ConsoleKey.Escape));

        Assert.Null(combo.Popup);
        Assert.Equal("Water", combo.SelectedItem);
        Assert.Equal(0, chosen);

        screen.Render(new TuiSize(30, 8));

        Assert.True(combo.IsFocused);
    }

    [Fact]
    public void The_list_opens_under_the_field_rather_than_in_the_middle()
    {
        var combo = Sample();
        var root = new TuiStack(TuiOrientation.Vertical)
        {
            Items = [new TuiTextWidget("above"), combo, new TuiTextWidget("below") { Size = "*" }],
        };

        using var screen = new TuiDeclarativeScreen(root, []);

        screen.Render(new TuiSize(40, 10));
        combo.OnInput(Named(ConsoleKey.DownArrow));

        var rows = screen.Render(new TuiSize(40, 10)).ToPlainText().Split('\n');
        var opened = Array.FindIndex(rows, row => row.Contains("Sodium", StringComparison.Ordinal));

        Assert.True(opened > combo.Bounds.Top, $"The list opened at row {opened}, not below row {combo.Bounds.Top}.");
    }

    [Fact]
    public void It_shows_one_property_of_an_object_when_told_which()
    {
        var combo = new TuiCombo([new { Name = "Sodium", Kelvin = 1156 }]) { DisplayProperty = "Name" };

        Assert.Equal("Sodium", combo.Text);
    }

    [Fact]
    public void Nothing_to_choose_from_shows_a_placeholder_and_does_not_open()
    {
        var combo = new TuiCombo { Placeholder = "none" };

        combo.OnInput(Named(ConsoleKey.DownArrow));

        Assert.Null(combo.Popup);
        Assert.Equal("none", combo.Text);
    }

    [Fact]
    public void A_combo_written_in_markup_is_the_same_combo()
    {
        var widget = TuiTreeBuilder.Build(new Dictionary<string, object?>
        {
            ["Combo"] = new object?[] { "Water", "Sodium" },
            ["Placeholder"] = "pick",
        });

        var combo = Assert.IsType<TuiCombo>(widget);

        Assert.Equal(["Water", "Sodium"], combo.Items);
        Assert.Equal("Water", combo.Text);
    }
}
