using Tosh.Tui;
using Tosh.Tui.Rendering;
using Tosh.Tui.Widgets;

namespace Tosh.Tests;

/// <summary>
/// Which entries to use, and in what order — <c>TUI-0002</c>.
/// </summary>
/// <remarks>
/// <see cref="TuiOrderedToggleEditorState{TItem}"/> keeps the order and the inclusions and
/// refuses a toggle that would go below a minimum, and does not draw. The state asks for an
/// updater returning a <em>new</em> entry carrying the new state, which a list of strings
/// cannot do — so the widget tracks inclusion beside the entries and hands back the entries
/// it was given.
/// </remarks>
public sealed class TuiArrangeTests
{
    private const int Width = 28;
    private const int Height = 5;

    private static TuiArrange Laid(TuiArrange arrange)
    {
        arrange.Measure(new TuiConstraints(Width, Height));
        arrange.Arrange(new TuiRect(0, 0, Width, Height));

        return arrange;
    }

    private static string Render(TuiArrange arrange)
    {
        var buffer = new TuiBuffer(new TuiSize(Width, Height));

        Laid(arrange).Draw(new TuiSurface(buffer, new TuiRect(0, 0, Width, Height)));

        return string.Join('\n', Enumerable.Range(0, Height).Select(row => buffer.RowText(row).TrimEnd()));
    }

    private static bool Press(TuiArrange arrange, ConsoleKey key, bool shift = false)
        => arrange.OnInput(TuiInputEvent.FromKey(new ConsoleKeyInfo('\0', key, shift, false, false)));

    [Fact]
    public void Every_entry_starts_in_and_is_drawn_with_its_box()
    {
        var text = Render(new TuiArrange(["Time", "Git"]));

        Assert.Contains("> [x] Time", text, StringComparison.Ordinal);
        Assert.Contains("  [x] Git", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Space_takes_an_entry_out_and_reports_what_is_left()
    {
        IReadOnlyList<object?>? reported = null;
        var arrange = Laid(new TuiArrange(["Time", "Git"]) { Changed = included => reported = included });

        Assert.True(Press(arrange, ConsoleKey.Spacebar));

        Assert.Equal(["Git"], arrange.Included);
        Assert.Equal(["Git"], reported);
        Assert.Contains("> [ ] Time", Render(arrange), StringComparison.Ordinal);
    }

    /// <summary>Shift with an arrow moves the entry, not the cursor.</summary>
    [Fact]
    public void Shift_and_an_arrow_move_the_entry()
    {
        var arrange = Laid(new TuiArrange(["Time", "Git"]));

        Assert.True(Press(arrange, ConsoleKey.DownArrow, shift: true));

        Assert.Equal(["Git", "Time"], arrange.Entries);
        Assert.Equal(["Git", "Time"], arrange.Included);
    }

    /// <summary>A plain arrow moves the cursor and leaves the order alone.</summary>
    [Fact]
    public void A_plain_arrow_leaves_the_order_alone()
    {
        var arrange = Laid(new TuiArrange(["Time", "Git"]));

        Assert.True(Press(arrange, ConsoleKey.DownArrow));

        Assert.Equal(["Time", "Git"], arrange.Entries);
        Assert.Contains("> [x] Git", Render(arrange), StringComparison.Ordinal);
    }

    /// <summary>A prompt with nothing in it is not a prompt, so the last one cannot leave.</summary>
    [Fact]
    public void A_toggle_below_the_minimum_is_refused()
    {
        object? refused = null;
        var arrange = Laid(new TuiArrange(["Time"]) { MinimumIncluded = 1, Refused = entry => refused = entry });

        Assert.True(Press(arrange, ConsoleKey.Spacebar));

        Assert.Equal(["Time"], arrange.Included);
        Assert.Equal("Time", refused);
    }

    /// <summary>Below the minimum is refused; back above it is not.</summary>
    [Fact]
    public void A_toggle_that_stays_above_the_minimum_is_applied()
    {
        var arrange = Laid(new TuiArrange(["Time", "Git"]) { MinimumIncluded = 1 });

        Press(arrange, ConsoleKey.Spacebar);

        Assert.Equal(["Git"], arrange.Included);
    }

    [Fact]
    public void Enter_settles_the_arrangement()
    {
        IReadOnlyList<object?>? committed = null;
        var arrange = Laid(new TuiArrange(["Time", "Git"]) { Committed = included => committed = included });

        Press(arrange, ConsoleKey.DownArrow, shift: true);
        Assert.True(Press(arrange, ConsoleKey.Enter));

        Assert.Equal(["Git", "Time"], committed);
    }

    [Fact]
    public void Escape_abandons_it()
    {
        var cancelled = false;
        var arrange = Laid(new TuiArrange(["Time"]) { Cancelled = () => cancelled = true });

        Assert.True(Press(arrange, ConsoleKey.Escape));
        Assert.True(cancelled);
    }

    /// <summary>
    /// The entries handed in are the entries handed back — a list of strings stays a list of
    /// strings, which is the reason inclusion is tracked beside them rather than on them.
    /// </summary>
    [Fact]
    public void The_entries_handed_back_are_the_ones_handed_in()
    {
        var arrange = Laid(new TuiArrange(["Time", "Git"]));

        Press(arrange, ConsoleKey.DownArrow, shift: true);

        Assert.All(arrange.Included, entry => Assert.IsType<string>(entry));
    }

    /// <summary>An empty widget answers its keys rather than passing them on.</summary>
    [Fact]
    public void An_empty_arrangement_still_answers_its_keys()
        => Assert.True(Press(Laid(new TuiArrange()), ConsoleKey.Spacebar));

    /// <summary>A key with nothing to do here is left for whatever else wants it.</summary>
    [Fact]
    public void An_unrelated_key_is_not_swallowed()
        => Assert.False(Press(Laid(new TuiArrange(["Time"])), ConsoleKey.F5));
}
