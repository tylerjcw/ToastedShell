using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// Which terminal TōSh decided it is talking to.
///
/// The keyboard, focus and paste protocols were switched on for every terminal and given an
/// environment variable to switch off again, while the graphics protocol was worked out from
/// the environment — so there was no one answer to "what is this terminal", nothing a script
/// could read, and no way to correct a wrong guess.
/// </summary>
public class TerminalProfileTests
{
    private static Func<string, string?> Env(params (string Name, string Value)[] entries)
        => name => entries.FirstOrDefault(e => e.Name == name).Value;

    [Fact]
    public void Ghostty_is_recognised_by_its_own_marker()
    {
        var profile = TerminalProfile.Detect(Env(("GHOSTTY_BIN_DIR", "/usr/bin"), ("TERM", "xterm-256color")));

        Assert.Equal("ghostty", profile.Name);
        Assert.True(profile.KittyKeyboard);
        Assert.Equal("kitty", profile.Graphics);
    }

    /// <summary>
    /// A terminal usually sets `TERM` to `xterm-256color` for the sake of software that
    /// expects it, so its own marker has to win or every such terminal reads as xterm.
    /// </summary>
    [Theory]
    [InlineData("KITTY_WINDOW_ID", "1", "kitty")]
    [InlineData("WEZTERM_PANE", "0", "wezterm")]
    [InlineData("ALACRITTY_WINDOW_ID", "1", "alacritty")]
    [InlineData("KONSOLE_VERSION", "220400", "konsole")]
    public void A_terminals_own_marker_beats_a_generic_TERM(string variable, string value, string expected)
    {
        var profile = TerminalProfile.Detect(Env((variable, value), ("TERM", "xterm-256color")));

        Assert.Equal(expected, profile.Name);
    }

    [Theory]
    [InlineData("WezTerm", "wezterm")]
    [InlineData("iTerm.app", "iterm2")]
    [InlineData("Apple_Terminal", "apple-terminal")]
    [InlineData("vscode", "vscode")]
    public void TERM_PROGRAM_names_the_terminal_when_there_is_no_marker(string program, string expected)
    {
        Assert.Equal(expected, TerminalProfile.Detect(Env(("TERM_PROGRAM", program))).Name);
    }

    /// <summary>
    /// A multiplexer relays what it understands and swallows the rest, so asking for a
    /// protocol it does not pass through sends the request to the pane instead.
    /// </summary>
    [Theory]
    [InlineData("TMUX", "/tmp/tmux-1000/default", "tmux")]
    [InlineData("TERM", "screen-256color", "screen")]
    public void A_multiplexer_is_not_asked_for_the_protocols_it_does_not_relay(string variable, string value, string expected)
    {
        var profile = TerminalProfile.Detect(Env((variable, value)));

        Assert.True(profile.Multiplexed);
        Assert.Equal(expected, profile.Name);
        Assert.False(profile.KittyKeyboard);
        Assert.False(profile.FocusReporting);
        Assert.Equal("half-blocks", profile.Graphics);
    }

    /// <summary>A terminal that answers for almost nothing is asked for almost nothing.</summary>
    [Fact]
    public void A_dumb_terminal_is_asked_for_no_protocols_at_all()
    {
        var profile = TerminalProfile.Detect(Env(("TERM", "dumb")));

        Assert.Equal("dumb", profile.Name);
        Assert.Equal(TerminalSurface.Basic, profile.Surface);
        Assert.False(profile.KittyKeyboard);
        Assert.False(profile.Unicode);
        Assert.Equal("none", profile.Graphics);
    }

    /// <summary>An unnamed terminal in a graphical session still gets what it can ignore.</summary>
    [Fact]
    public void An_unrecognised_window_still_gets_the_protocols_it_can_ignore()
    {
        var profile = TerminalProfile.Detect(Env(("TERM", "xterm-256color"), ("DISPLAY", ":0")));

        Assert.Equal(TerminalSurface.Window, profile.Surface);
        Assert.True(profile.KittyKeyboard);
    }

    [Fact]
    public void COLORTERM_settles_true_colour_for_a_terminal_nothing_else_names()
    {
        Assert.True(TerminalProfile.Detect(Env(("COLORTERM", "truecolor"), ("DISPLAY", ":0"))).TrueColor);
        Assert.False(TerminalProfile.Detect(Env(("TERM", "vt100"))).TrueColor);

        // Except on a console, where the kernel decides and a profile's export is a claim
        // the console cannot honour.
        Assert.False(TerminalProfile.Detect(Env(("TERM", "linux"), ("COLORTERM", "truecolor"))).TrueColor);
    }

    // ── the terminals covered ──

    /// <summary>Each terminal that announces itself is recognised by its own variable.</summary>
    [Theory]
    [InlineData("GHOSTTY_RESOURCES_DIR", "ghostty")]
    [InlineData("GHOSTTY_BIN_DIR", "ghostty")]
    [InlineData("KITTY_WINDOW_ID", "kitty")]
    [InlineData("WEZTERM_PANE", "wezterm")]
    [InlineData("WEZTERM_EXECUTABLE", "wezterm")]
    [InlineData("ALACRITTY_WINDOW_ID", "alacritty")]
    [InlineData("ALACRITTY_SOCKET", "alacritty")]
    [InlineData("KONSOLE_VERSION", "konsole")]
    [InlineData("KONSOLE_DBUS_SESSION", "konsole")]
    [InlineData("ITERM_SESSION_ID", "iterm2")]
    [InlineData("TERMINATOR_UUID", "terminator")]
    [InlineData("TILIX_ID", "tilix")]
    [InlineData("WT_SESSION", "windows-terminal")]
    [InlineData("MLTERM", "mlterm")]
    [InlineData("XTERM_VERSION", "xterm")]
    public void Each_terminal_that_announces_itself_is_recognised(string variable, string expected)
    {
        Assert.Equal(expected, TerminalProfile.Detect(Env((variable, "1"), ("TERM", "xterm-256color"))).Name);
    }

    /// <summary>Terminals identified by the name in TERM, where they set nothing else.</summary>
    [Theory]
    [InlineData("foot", "foot")]
    [InlineData("contour", "contour")]
    [InlineData("rio", "rio")]
    [InlineData("wayst", "wayst")]
    [InlineData("zutty", "zutty")]
    [InlineData("st-256color", "st")]
    [InlineData("rxvt-unicode-256color", "urxvt")]
    [InlineData("ms-terminal", "windows-terminal")]
    [InlineData("putty", "putty")]
    [InlineData("mintty", "mintty")]
    [InlineData("cygwin", "cygwin")]
    [InlineData("xterm-256color", "xterm")]
    public void Terminals_are_identified_by_the_name_in_TERM(string term, string expected)
    {
        Assert.Equal(expected, TerminalProfile.Detect(Env(("TERM", term), ("DISPLAY", ":0"))).Name);
    }

    /// <summary>
    /// A VTE terminal sets `TERM=xterm-256color` for the sake of software that expects it, so
    /// reading `TERM` first makes every GNOME-family terminal an xterm — and gives it xterm's
    /// sixel, which VTE does not have.
    /// </summary>
    [Fact]
    public void A_vte_terminal_is_not_mistaken_for_an_xterm()
    {
        var profile = TerminalProfile.Detect(
            Env(("VTE_VERSION", "7600"), ("TERM", "xterm-256color"), ("DISPLAY", ":0")));

        Assert.Equal("vte", profile.Name);
        Assert.Equal("half-blocks", profile.Graphics);
        Assert.True(profile.TrueColor);
    }

    /// <summary>
    /// A console has no graphical environment behind it: a fixed font in a fixed palette, and
    /// no protocol to ask for. Asking anyway writes the request across the screen.
    /// </summary>
    [Theory]
    [InlineData("linux", "linux-console")]
    [InlineData("fbterm", "fbterm")]
    [InlineData("bterm", "bterm")]
    [InlineData("kmscon", "kmscon")]
    public void A_kernel_console_is_asked_for_nothing_a_window_would_get(string term, string expected)
    {
        var profile = TerminalProfile.Detect(Env(("TERM", term)));

        Assert.Equal(expected, profile.Name);
        Assert.Equal(TerminalSurface.Console, profile.Surface);
        Assert.True(profile.Console);
        Assert.False(profile.TrueColor);
        Assert.Equal(16, profile.Colors);
        Assert.False(profile.Unicode);
        Assert.False(profile.KittyKeyboard);
        Assert.False(profile.FocusReporting);
        Assert.Equal("none", profile.Graphics);
    }

    /// <summary>Only the terminals that have one claim a picture protocol.</summary>
    [Theory]
    [InlineData("ghostty", "kitty")]
    [InlineData("kitty", "kitty")]
    [InlineData("wezterm", "kitty")]
    [InlineData("konsole", "kitty")]
    [InlineData("foot", "sixel")]
    [InlineData("contour", "sixel")]
    [InlineData("iterm2", "sixel")]
    [InlineData("xterm", "sixel")]
    [InlineData("alacritty", "half-blocks")]
    [InlineData("vte", "half-blocks")]
    [InlineData("st", "half-blocks")]
    public void The_picture_protocol_follows_the_terminal(string name, string expected)
    {
        Assert.Equal(expected, TerminalProfile.ForSurface(name, TerminalSurface.Window).Graphics);
    }

    /// <summary>Being reached over SSH is recorded, and changes nothing else by itself.</summary>
    [Fact]
    public void A_terminal_reached_over_ssh_says_so()
    {
        var profile = TerminalProfile.Detect(
            Env(("SSH_TTY", "/dev/pts/3"), ("KITTY_WINDOW_ID", "1"), ("TERM", "xterm-kitty")));

        Assert.True(profile.Remote);
        Assert.Equal("kitty", profile.Name);
        Assert.True(profile.KittyKeyboard);
    }

    // ── size ──

    /// <summary>
    /// The size is asked for each time rather than recorded, because a window is resized while
    /// TōSh runs. Reading it throws when output is redirected and answers zero in some
    /// consoles, and a caller laying out text wants a number either way.
    /// </summary>
    [Fact]
    public void The_size_is_always_a_usable_number()
    {
        var profile = TerminalProfile.Detect(Env(("TERM", "xterm-256color")));

        Assert.True(profile.Width > 0);
        Assert.True(profile.Height > 0);
    }

    /// <summary>Size is measured, not recorded, so it is not part of what makes two profiles equal.</summary>
    [Fact]
    public void Size_is_not_part_of_the_profiles_identity()
    {
        var one = TerminalProfile.Detect(Env(("GHOSTTY_BIN_DIR", "/usr/bin")));
        var two = TerminalProfile.Detect(Env(("GHOSTTY_BIN_DIR", "/usr/bin")));

        Assert.Equal(one, two);
    }

    // ── what the reader said ──

    [Fact]
    public void Naming_the_terminal_re_derives_what_it_can_do()
    {
        var detected = TerminalProfile.Detect(Env(("TERM", "vt100")));
        var configured = new ToshTerminalConfig { Name = "ghostty" }.Over(detected);

        Assert.Equal("ghostty", configured.Name);
        Assert.Equal("kitty", configured.Graphics);
        Assert.True(configured.TrueColor);
    }

    [Fact]
    public void One_capability_can_be_corrected_without_disturbing_the_rest()
    {
        var detected = TerminalProfile.Detect(Env(("GHOSTTY_BIN_DIR", "/usr/bin")));
        var configured = new ToshTerminalConfig { Graphics = "sixel" }.Over(detected);

        Assert.Equal("ghostty", configured.Name);
        Assert.Equal("sixel", configured.Graphics);
        Assert.True(configured.KittyKeyboard);
    }

    [Fact]
    public void An_empty_configuration_changes_nothing()
    {
        var detected = TerminalProfile.Detect(Env(("GHOSTTY_BIN_DIR", "/usr/bin")));

        Assert.Equal(detected, new ToshTerminalConfig().Over(detected));
    }

    [Fact]
    public void Resetting_returns_to_what_was_detected()
    {
        var detected = TerminalProfile.Detect(Env(("GHOSTTY_BIN_DIR", "/usr/bin")));
        var config = new ToshTerminalConfig { Name = "xterm", Graphics = "sixel" };

        config.Reset();

        Assert.Equal(detected, config.Over(detected));
    }
}
