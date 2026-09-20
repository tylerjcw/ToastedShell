using Tosh.Tui;
using Tosh.Tui.Rendering;
using Tosh.Tui.Widgets;

namespace Tosh.Tests;

/// <summary>
/// One choice out of several, drawn as radio rows — <c>TUI-0002</c>.
/// </summary>
/// <remarks>
/// <see cref="TuiOptionPickerState{TItem}"/> keeps the mark, keeps its key so a redraw finds
/// it again, and reads the keys — and does not draw. The config browser drew its enum picker
/// a row at a time in the middle of a detail pane. This is that drawing, owned by a widget,
/// wrapping the same state so the keys mean what they already meant.
/// </remarks>
public sealed class TuiChoiceTests
{
    private const int Width = 24;

    private static TuiChoice Laid(TuiChoice choice, int height = 4)
    {
        choice.Measure(new TuiConstraints(Width, height));
        choice.Arrange(new TuiRect(0, 0, Width, height));

        return choice;
    }

    private static string Render(TuiChoice choice, int height = 4)
    {
        var buffer = new TuiBuffer(new TuiSize(Width, height));

        Laid(choice, height).Draw(new TuiSurface(buffer, new TuiRect(0, 0, Width, height)));

        return string.Join('\n', Enumerable.Range(0, height).Select(row => buffer.RowText(row).TrimEnd()));
    }

    private static bool Press(TuiChoice choice, ConsoleKey key)
        => choice.OnInput(TuiInputEvent.FromKey(new ConsoleKeyInfo('\0', key, false, false, false)));

    /// <summary>
    /// The mark says which option *is* the value, not merely where the cursor is — which is
    /// the whole difference from a list.
    /// </summary>
    [Fact]
    public void The_marked_option_is_drawn_as_the_chosen_one()
    {
        var text = Render(new TuiChoice(["Always", "Never"]));

        Assert.Contains("> (*) Always", text, StringComparison.Ordinal);
        Assert.Contains("  ( ) Never", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ConsoleKey.DownArrow, 1)]
    [InlineData(ConsoleKey.End, 2)]
    public void The_keys_move_the_mark(ConsoleKey key, int expected)
    {
        var choice = Laid(new TuiChoice(["a", "b", "c"]));

        Assert.True(Press(choice, key));
        Assert.Equal(expected, choice.SelectedIndex);
    }

    /// <summary>Moving the mark is not choosing; nothing is settled until Enter.</summary>
    [Fact]
    public void Moving_the_mark_does_not_choose()
    {
        object? chosen = null;
        object? changed = null;
        var choice = Laid(new TuiChoice(["a", "b"])
        {
            Chosen = item => chosen = item,
            SelectionChanged = item => changed = item,
        });

        Press(choice, ConsoleKey.DownArrow);

        Assert.Null(chosen);
        Assert.Equal("b", changed);
    }

    [Fact]
    public void Enter_settles_the_marked_option()
    {
        object? chosen = null;
        var choice = Laid(new TuiChoice(["a", "b"]) { Chosen = item => chosen = item });

        Press(choice, ConsoleKey.DownArrow);
        Assert.True(Press(choice, ConsoleKey.Enter));
        Assert.Equal("b", chosen);
    }

    [Fact]
    public void Escape_abandons_without_choosing()
    {
        object? chosen = null;
        var cancelled = false;
        var choice = Laid(new TuiChoice(["a"]) { Chosen = item => chosen = item, Cancelled = () => cancelled = true });

        Assert.True(Press(choice, ConsoleKey.Escape));
        Assert.True(cancelled);
        Assert.Null(chosen);
    }

    /// <summary>A key with nothing to do here is left for whatever else wants it.</summary>
    [Fact]
    public void An_unrelated_key_is_not_swallowed()
        => Assert.False(Press(Laid(new TuiChoice(["a"])), ConsoleKey.F5));

    /// <summary>And a picker with nothing to pick answers nothing at all.</summary>
    [Fact]
    public void An_empty_picker_handles_nothing()
        => Assert.False(Press(Laid(new TuiChoice()), ConsoleKey.Enter));

    /// <summary>
    /// The state keeps the mark by key, so re-offering the same options leaves the choice
    /// where the reader put it.
    /// </summary>
    [Fact]
    public void Re_offering_the_same_options_keeps_the_mark()
    {
        var choice = Laid(new TuiChoice(["a", "b", "c"]));

        Press(choice, ConsoleKey.DownArrow);
        Assert.Equal("b", choice.Selected);

        choice.Options = ["a", "b", "c"];

        Assert.Equal("b", choice.Selected);
    }

    /// <summary>A list of records is labelled by a property rather than by ToString.</summary>
    [Fact]
    public void A_display_property_labels_the_options()
    {
        var text = Render(new TuiChoice([new Option("Dark"), new Option("Light")])
        {
            DisplayProperty = "Name",
        });

        Assert.Contains("Dark", text, StringComparison.Ordinal);
        Assert.Contains("Light", text, StringComparison.Ordinal);
    }

    private sealed record Option(string Name);
}
