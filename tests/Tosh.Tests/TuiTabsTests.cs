using Tosh.Tui;
using Tosh.Tui.Rendering;
using Tosh.Tui.Widgets;

namespace Tosh.Tests;

/// <summary>
/// Several views in one rectangle, one at a time (<c>TUI-0002</c>).
/// </summary>
/// <remarks>
/// A script could build this out of a stack and a <c>When</c> predicate per child, and would
/// then own the selection, the labels, the highlighting and the keys — four things to get
/// right for a shape every toolkit has.
/// </remarks>
public sealed class TuiTabsTests
{
    private static TuiTabs Three() => new TuiTabs()
        .Add("Summary", new TuiTextWidget("the summary"))
        .Add("Log", new TuiTextWidget("the log"))
        .Add("Errors", new TuiTextWidget("the errors"));

    private static IReadOnlyList<string> Render(TuiWidget widget, int width, int height)
    {
        var buffer = new TuiBuffer(new TuiSize(width, height));

        widget.Measure(new TuiConstraints(width, height));
        widget.Arrange(new TuiRect(0, 0, width, height));
        widget.Draw(new TuiSurface(buffer, new TuiRect(0, 0, width, height)));

        return [.. Enumerable.Range(0, height).Select(row => buffer.RowText(row).TrimEnd())];
    }

    private static TuiInputEvent Key(ConsoleKey key)
        => TuiInputEvent.FromKey(new ConsoleKeyInfo('\0', key, false, false, false));

    [Fact]
    public void The_labels_are_drawn_and_the_selected_content_under_them()
    {
        var rows = Render(Three(), 40, 5);

        Assert.Equal("Summary  Log  Errors", rows[0]);
        Assert.Equal("the summary", rows[2]);
    }

    /// <summary>Only the tab showing is drawn.</summary>
    /// <remarks>
    /// The reason this is worth having over a stack of hidden children: a tab that is not on
    /// screen costs nothing to draw, which is what makes it usable for an expensive pane.
    /// </remarks>
    [Fact]
    public void Nothing_but_the_selected_tab_is_drawn()
    {
        var rows = Render(Three(), 40, 5);

        Assert.DoesNotContain(rows, row => row.Contains("the log", StringComparison.Ordinal));
        Assert.DoesNotContain(rows, row => row.Contains("the errors", StringComparison.Ordinal));
    }

    [Fact]
    public void Right_and_left_move_between_tabs()
    {
        var tabs = Three();

        Assert.True(tabs.OnInput(Key(ConsoleKey.RightArrow)));
        Assert.Equal(1, tabs.Selected);

        Assert.True(tabs.OnInput(Key(ConsoleKey.LeftArrow)));
        Assert.Equal(0, tabs.Selected);
    }

    /// <summary>
    /// The row of tabs is a ring.
    /// </summary>
    /// <remarks>
    /// Stopping at the last one means a reader holding right has to work out which way to
    /// go back, which is a thing to think about where there should not be one.
    /// </remarks>
    [Fact]
    public void Moving_past_the_end_comes_back_to_the_beginning()
    {
        var tabs = Three();

        tabs.OnInput(Key(ConsoleKey.LeftArrow));
        Assert.Equal(2, tabs.Selected);

        tabs.OnInput(Key(ConsoleKey.RightArrow));
        Assert.Equal(0, tabs.Selected);
    }

    [Fact]
    public void Home_and_End_go_to_the_ends_without_wrapping()
    {
        var tabs = Three();

        tabs.OnInput(Key(ConsoleKey.End));
        Assert.Equal(2, tabs.Selected);

        Assert.False(tabs.OnInput(Key(ConsoleKey.End)));

        tabs.OnInput(Key(ConsoleKey.Home));
        Assert.Equal(0, tabs.Selected);
    }

    [Fact]
    public void Choosing_a_tab_says_so()
    {
        var chosen = new List<int>();
        var tabs = Three();

        tabs.Changed = chosen.Add;

        tabs.OnInput(Key(ConsoleKey.RightArrow));
        tabs.OnInput(Key(ConsoleKey.RightArrow));

        Assert.Equal([1, 2], chosen);
    }

    /// <summary>A key that changes nothing is declined, so something else can have it.</summary>
    [Fact]
    public void A_key_that_moves_nothing_is_not_consumed()
    {
        Assert.False(Three().OnInput(Key(ConsoleKey.UpArrow)));
        Assert.False(new TuiTabs().OnInput(Key(ConsoleKey.RightArrow)));
    }

    /// <summary>
    /// Every tab's content stays in the tree, not only the one showing.
    /// </summary>
    /// <remarks>
    /// Focus, hit testing and the visibility pass all walk <c>Children</c>. A tab reporting
    /// only what is showing would have its other panes leave the tree — which is how a
    /// binding on a hidden tab stops being asked and comes back stale.
    /// </remarks>
    [Fact]
    public void Every_tab_stays_in_the_tree_but_only_one_takes_the_keyboard()
    {
        var tabs = Three();

        Assert.Equal(3, tabs.Children.Count);
        Assert.Single(tabs.FocusChildren);
        Assert.Same(tabs.Children[0], tabs.FocusChildren[0]);
    }

    [Fact]
    public void The_value_a_form_reads_is_the_label_showing()
    {
        var tabs = Three();

        Assert.Equal("Summary", tabs.Value);

        tabs.OnInput(Key(ConsoleKey.End));

        Assert.Equal("Errors", tabs.Value);
    }

    [Fact]
    public void Selecting_out_of_range_is_clamped()
    {
        var tabs = Three();

        tabs.Selected = 99;
        Assert.Equal(2, tabs.Selected);

        tabs.Selected = -4;
        Assert.Equal(0, tabs.Selected);
    }

    // ── What a frame around them says ────────────────────────────────

    /// <summary>A border with no title of its own says which tab is showing.</summary>
    [Fact]
    public void A_frame_around_tabs_is_titled_with_the_tab_showing()
    {
        var tabs = Three();
        var border = new TuiBorder(tabs);

        Assert.Contains("Summary", Render(border, 30, 6)[0], StringComparison.Ordinal);

        tabs.OnInput(Key(ConsoleKey.End));

        Assert.Contains("Errors", Render(border, 30, 6)[0], StringComparison.Ordinal);
    }

    /// <summary>A border that was told what to say keeps saying it.</summary>
    /// <remarks>
    /// Told wins. A frame given a title is saying something the author chose, and a child
    /// with an opinion does not overrule it.
    /// </remarks>
    [Fact]
    public void A_frame_that_was_given_a_title_keeps_it()
    {
        var border = new TuiBorder(Three(), "Diagnostics");

        var drawn = Render(border, 30, 6)[0];

        Assert.Contains("Diagnostics", drawn, StringComparison.Ordinal);
        Assert.DoesNotContain("Summary", drawn, StringComparison.Ordinal);
    }

    /// <summary>And the tabs themselves can be told to stop tracking.</summary>
    [Fact]
    public void Tabs_given_a_title_stop_naming_the_tab()
    {
        var tabs = Three();

        tabs.Title = "Panes";

        Assert.Equal("Panes", tabs.Caption);

        tabs.OnInput(Key(ConsoleKey.End));

        Assert.Equal("Panes", tabs.Caption);
    }

    [Fact]
    public void Tabs_with_nothing_in_them_offer_no_caption()
        => Assert.Null(new TuiTabs().Caption);
}
