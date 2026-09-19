using Tosh.Tui;
using Tosh.Tui.Rendering;
using Tosh.Tui.Widgets;

namespace Tosh.Tests;

/// <summary>
/// Telling pasted text apart from typing.
/// </summary>
/// <remarks>
/// <para>
/// Without mode 2004 the two are the same thing: pasting three lines into a field delivers
/// the newlines as presses of Enter, so the first line submits the form and the rest is read
/// as more typing. That is not a key anyone pressed — it is a character in something they
/// copied.
/// </para>
/// <para>
/// The reader has to understand the brackets before the mode is asked for. A terminal
/// sending them to something that does not parse them types <c>[200~</c> into whatever has
/// focus, which is worse than leaving the mode off.
/// </para>
/// </remarks>
public sealed class TuiBracketedPasteTests
{
    private static Func<string, string?> Env(params (string Name, string Value)[] entries)
    {
        var map = entries.ToDictionary(e => e.Name, e => e.Value, StringComparer.Ordinal);

        return name => map.TryGetValue(name, out var value) ? value : null;
    }

    /// <summary>The sequences are the ones mode 2004 is spelled with.</summary>
    /// <remarks>
    /// Pinned because a wrong number is invisible: the terminal ignores a private mode it
    /// does not recognise, so it looks exactly like a terminal without support.
    /// </remarks>
    [Fact]
    public void The_sequences_are_mode_2004()
    {
        Assert.Equal("\x1b[?2004h", TuiBracketedPaste.Enable);
        Assert.Equal("\x1b[?2004l", TuiBracketedPaste.Disable);
        Assert.Equal("\x1b[200~", TuiBracketedPaste.Start);
        Assert.Equal("\x1b[201~", TuiBracketedPaste.Finish);
    }

    [Fact]
    public void It_is_on_unless_turned_off()
    {
        Assert.True(TuiBracketedPaste.Detect(Env()));
        Assert.False(TuiBracketedPaste.Detect(Env(("TOSH_TUI_PASTE", "0"))));
        Assert.False(TuiBracketedPaste.Detect(Env(("TOSH_TUI_PASTE", "off"))));
        Assert.True(TuiBracketedPaste.Detect(Env(("TOSH_TUI_PASTE", "1"))));
    }

    /// <summary>A paste is its own kind of event, carrying text and no key.</summary>
    [Fact]
    public void A_paste_is_neither_a_key_nor_a_mouse_event()
    {
        var paste = TuiInputEvent.FromPaste("hello");

        Assert.True(paste.IsPaste);
        Assert.False(paste.IsKey);
        Assert.False(paste.IsMouse);
        Assert.Equal("hello", paste.Text);
    }

    /// <summary>
    /// A paste is not a click at the origin.
    /// </summary>
    /// <remarks>
    /// The regression this is really for. Six widgets read "not a key" as "therefore a
    /// mouse event" and went straight to <c>input.Mouse</c> — which on a paste is a default
    /// struct, and <c>TuiMouseAction.Press</c> is zero. So a paste arrived as a press of
    /// the left button at column 0, row 0: focus jumped to the top-left corner and the text
    /// went wherever that was. The condition they meant was "is this a mouse event", which
    /// only coincided with "not a key" while those were the only two kinds.
    /// </remarks>
    [Fact]
    public void A_paste_does_not_select_the_first_item_of_a_list()
    {
        var list = new TuiList(["first", "second", "third"]) { SelectedIndex = 2 };

        list.Measure(new TuiConstraints(20, 3));
        list.Arrange(new TuiRect(0, 0, 20, 3));

        Assert.False(list.OnInput(TuiInputEvent.FromPaste("pasted")));
        Assert.Equal(2, list.SelectedIndex);
    }

    /// <summary>A single-line field takes a pasted newline as a space.</summary>
    /// <remarks>
    /// Rather than dropping what follows it. Someone pasting a wrapped address into a
    /// one-line box means all of it, and silently keeping the first line is the version of
    /// this that looks like it worked.
    /// </remarks>
    [Fact]
    public void A_single_line_field_flattens_a_pasted_newline()
    {
        var field = Focused(new TuiTextField(string.Empty));

        Assert.True(field.OnInput(TuiInputEvent.FromPaste("alpha\nbeta")));
        Assert.Equal("alpha beta", field.Text);
    }

    /// <summary>A multiline field keeps the newlines.</summary>
    [Fact]
    public void A_multiline_field_keeps_pasted_newlines()
    {
        var field = Focused(new TuiTextField(string.Empty) { Multiline = true });

        Assert.True(field.OnInput(TuiInputEvent.FromPaste("alpha\nbeta")));
        Assert.Equal("alpha\nbeta", field.Text);
    }

    /// <summary>Whatever the terminal used for line endings.</summary>
    [Fact]
    public void Carriage_returns_do_not_survive_a_paste()
    {
        var field = Focused(new TuiTextField(string.Empty) { Multiline = true });

        Assert.True(field.OnInput(TuiInputEvent.FromPaste("alpha\r\nbeta")));
        Assert.Equal("alpha\nbeta", field.Text);
    }

    /// <summary>An unfocused field is not written into by someone else's paste.</summary>
    [Fact]
    public void An_unfocused_field_declines_a_paste()
    {
        var field = new TuiTextField(string.Empty);

        Assert.False(field.OnInput(TuiInputEvent.FromPaste("alpha")));
        Assert.Equal(string.Empty, field.Text);
    }

    private static TuiTextField Focused(TuiTextField field)
    {
        field.Measure(new TuiConstraints(40, 3));
        field.Arrange(new TuiRect(0, 0, 40, 3));
        field.IsFocused = true;

        return field;
    }
}
