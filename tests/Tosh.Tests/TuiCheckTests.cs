using Tosh.Tui;
using Tosh.Tui.Rendering;
using Tosh.Tui.Widgets;

namespace Tosh.Tests;

/// <summary>A checkbox: a label, a box, and the two ways to tick it.</summary>
public sealed class TuiCheckTests
{
    private static TuiCheck Laid(TuiCheck check, int width = 20)
    {
        check.Measure(new TuiConstraints(width, 1));
        check.Arrange(new TuiRect(0, 0, width, 1));

        return check;
    }

    private static string Render(TuiCheck check, int width = 20)
    {
        var buffer = new TuiBuffer(new TuiSize(width, 1));

        check.Draw(new TuiSurface(buffer, new TuiRect(0, 0, width, 1)));

        return buffer.RowText(0).TrimEnd();
    }

    [Fact]
    public void An_unchecked_box_draws_empty_and_a_checked_one_does_not()
    {
        var check = Laid(new TuiCheck("Enabled"));

        Assert.Equal("  [ ] Enabled", Render(check));

        check.IsChecked = true;

        Assert.Equal("  [x] Enabled", Render(check));
    }

    /// <summary>Focus shows the marker, so a reader can see what a keypress would hit.</summary>
    [Fact]
    public void Focus_shows_the_marker()
    {
        var check = Laid(new TuiCheck("Enabled")) ;

        check.IsFocused = true;

        Assert.StartsWith("> ", Render(check), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ConsoleKey.Spacebar, ' ')]
    [InlineData(ConsoleKey.Enter, '\r')]
    public void Space_and_enter_toggle_it(ConsoleKey key, char character)
    {
        var toggles = new List<bool>();
        var check = Laid(new TuiCheck("Enabled", false, toggles.Add));

        check.IsFocused = true;

        Assert.True(check.OnInput(TuiInputEvent.FromKey(new ConsoleKeyInfo(character, key, false, false, false))));
        Assert.True(check.IsChecked);
        Assert.Equal([true], toggles);
    }

    /// <summary>An unfocused checkbox is not toggled by someone else's keypress.</summary>
    [Fact]
    public void It_ignores_keys_while_unfocused()
    {
        var check = Laid(new TuiCheck("Enabled"));

        Assert.False(check.OnInput(TuiInputEvent.FromKey(new ConsoleKeyInfo(' ', ConsoleKey.Spacebar, false, false, false))));
        Assert.False(check.IsChecked);
    }

    [Fact]
    public void A_click_focuses_and_toggles_it()
    {
        var check = Laid(new TuiCheck("Enabled"));

        Assert.True(check.OnInput(TuiInputEvent.FromMouse(
            new TuiMouseEvent(TuiMouseAction.Press, TuiMouseButton.Left, 3, 0, false, false, false))));

        Assert.True(check.IsChecked);
    }

    /// <summary>
    /// A paste is not a click, and neither is a focus report.
    /// </summary>
    /// <remarks>
    /// The trap every widget in this framework fell into: reading "not a key" as "therefore
    /// a mouse event" and going straight to <c>input.Mouse</c>, which on those events is a
    /// default struct — and both <c>TuiMouseAction.Press</c> and <c>TuiMouseButton.Left</c>
    /// are zero. So a paste arrived as a left-click at the origin, and a checkbox sitting
    /// there ticked itself.
    /// </remarks>
    [Theory]
    [MemberData(nameof(NonMouseEvents))]
    public void It_is_not_toggled_by_events_that_are_not_clicks(TuiInputEvent input)
    {
        var toggles = new List<bool>();
        var check = Laid(new TuiCheck("Enabled", false, toggles.Add));

        Assert.False(check.OnInput(input));
        Assert.False(check.IsChecked);
        Assert.Empty(toggles);
    }

    public static TheoryData<TuiInputEvent> NonMouseEvents => new()
    {
        TuiInputEvent.FromPaste("pasted"),
        TuiInputEvent.FromFocus(true),
        TuiInputEvent.FromFocus(false),
    };
}
