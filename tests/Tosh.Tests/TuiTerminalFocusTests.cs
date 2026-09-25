using Tosh.Tui;
using Tosh.Tui.Rendering;

namespace Tosh.Tests;

/// <summary>
/// Knowing whether anyone is looking at the screen.
/// </summary>
/// <remarks>
/// Mode 1004 makes the terminal send <c>CSI I</c> when its window is focused and
/// <c>CSI O</c> when it is not, so a widget can dim what it shows or stop an animation.
/// The framework asks for the reports and delivers them; it does not change what it draws.
/// Skipping the refresh tick while unfocused was the obvious use and is the wrong one — a
/// tick re-samples as well as redraws, so a screen nobody is watching would quietly stop
/// keeping up with the thing it is watching.
/// </remarks>
public sealed class TuiTerminalFocusTests
{
    private static Func<string, string?> Env(params (string Name, string Value)[] entries)
    {
        var map = entries.ToDictionary(e => e.Name, e => e.Value, StringComparer.Ordinal);

        return name => map.TryGetValue(name, out var value) ? value : null;
    }

    /// <summary>The sequences are the ones mode 1004 is spelled with.</summary>
    [Fact]
    public void The_sequences_are_mode_1004()
    {
        Assert.Equal("\x1b[?1004h", TuiTerminalFocus.Enable);
        Assert.Equal("\x1b[?1004l", TuiTerminalFocus.Disable);
        Assert.Equal("\x1b[I", TuiTerminalFocus.Gained);
        Assert.Equal("\x1b[O", TuiTerminalFocus.Lost);
    }

    /// <summary>An explicit setting is the last word; otherwise the terminal decides.</summary>
    /// <remarks>
    /// This used to be the opt-out alone, so the request went to every terminal — including
    /// the multiplexers that do not relay it and the consoles that cannot honour it, where
    /// what reached the screen was the request itself.
    /// </remarks>
    [Fact]
    public void An_explicit_setting_wins_over_what_the_terminal_says()
    {
        Assert.False(TuiTerminalFocus.Detect(Env(("TOSH_TUI_FOCUS", "0"))));
        Assert.False(TuiTerminalFocus.Detect(Env(("TOSH_TUI_FOCUS", "off"))));
        Assert.True(TuiTerminalFocus.Detect(Env(("TOSH_TUI_FOCUS", "1"), ("TERM", "linux"))));
    }

    [Fact]
    public void A_windowed_terminal_is_asked_and_a_console_is_not()
    {
        Assert.True(TuiTerminalFocus.Detect(Env(("TERM", "xterm-256color"), ("DISPLAY", ":0"))));
        Assert.False(TuiTerminalFocus.Detect(Env(("TERM", "linux"))));
        Assert.False(TuiTerminalFocus.Detect(Env(("TMUX", "/tmp/t"))));
    }

    /// <summary>A focus report is its own kind, and carries which way it went.</summary>
    [Fact]
    public void A_focus_report_is_its_own_kind_of_event()
    {
        var gained = TuiInputEvent.FromFocus(true);
        var lost = TuiInputEvent.FromFocus(false);

        Assert.True(gained.IsFocus);
        Assert.True(gained.HasFocus);
        Assert.False(lost.HasFocus);

        // The distinction the widgets now depend on: a focus report is not a mouse event
        // with a default struct behind it, which is how a paste became a click at the origin.
        foreach (var report in (TuiInputEvent[])[gained, lost])
        {
            Assert.False(report.IsKey);
            Assert.False(report.IsMouse);
            Assert.False(report.IsPaste);
        }
    }
}
