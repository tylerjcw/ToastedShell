using Tosh.Tui;
using Tosh.Tui.Rendering;

namespace Tosh.Tests;

/// <summary>
/// The diffing writer: what actually reaches the terminal.
/// </summary>
public sealed class TuiTerminalWriterTests
{
    private static TuiBuffer Buffer(int width = 10, int height = 3) => new(new TuiSize(width, height));

    private static string Readable(string output) => output.Replace("\x1b", "\\e");

    [Fact]
    public void A_first_frame_clears_and_paints()
    {
        var buffer = Buffer();
        buffer.DrawText(0, 0, "hi", TuiStyle.Default);

        var output = TuiTerminalWriter.Present(buffer);

        Assert.StartsWith("\x1b[0m\x1b[2J\x1b[H", output, StringComparison.Ordinal);
        Assert.Contains("hi", output, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unchanged_frame_writes_nothing()
    {
        var previous = Buffer();
        previous.DrawText(0, 0, "steady", TuiStyle.Default);

        var next = Buffer();
        next.DrawText(0, 0, "steady", TuiStyle.Default);

        Assert.Equal(string.Empty, TuiTerminalWriter.Present(previous, next));
    }

    [Fact]
    public void Changing_one_cell_writes_that_cell_and_not_the_screen()
    {
        var previous = Buffer();
        previous.DrawText(0, 0, "0%", TuiStyle.Default);

        var next = Buffer();
        next.DrawText(0, 0, "9%", TuiStyle.Default);

        var output = TuiTerminalWriter.Present(previous, next);

        // One cursor move to row 1 column 1, the new character, and nothing else.
        Assert.Equal("\\e[1;1H9", Readable(output));
        Assert.DoesNotContain("2J", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Adjacent_changes_share_one_cursor_move()
    {
        var previous = Buffer();
        var next = Buffer();
        next.DrawText(3, 1, "abc", TuiStyle.Default);

        var output = TuiTerminalWriter.Present(previous, next);

        Assert.Equal("\\e[2;4Habc", Readable(output));
    }

    [Fact]
    public void Separated_changes_each_get_their_own_move()
    {
        var previous = Buffer();
        var next = Buffer();
        next.DrawText(0, 0, "a", TuiStyle.Default);
        next.DrawText(8, 0, "b", TuiStyle.Default);

        var output = TuiTerminalWriter.Present(previous, next);

        Assert.Equal("\\e[1;1Ha\\e[1;9Hb", Readable(output));
    }

    [Fact]
    public void A_resize_repaints_in_full()
    {
        var previous = Buffer(width: 10);
        var next = Buffer(width: 20);

        var output = TuiTerminalWriter.Present(previous, next);

        // Nothing about the old frame's addresses survives a resize.
        Assert.StartsWith("\x1b[0m\x1b[2J\x1b[H", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Styling_is_emitted_once_per_run_rather_than_per_cell()
    {
        var previous = Buffer();
        var next = Buffer();
        next.DrawText(0, 0, "red", new TuiStyle(Foreground: "red"));

        var output = TuiTerminalWriter.Present(previous, next);

        // One introducer for the run, not one per character.
        Assert.Equal(1, output.Split("\x1b[31m").Length - 1);
        Assert.Contains("red", output, StringComparison.Ordinal);
    }

    [Fact]
    public void A_style_change_is_reset_before_the_next_style()
    {
        var previous = Buffer();
        var next = Buffer();
        next.DrawText(0, 0, "a", new TuiStyle(Foreground: "red"));
        next.DrawText(1, 0, "b", new TuiStyle(Foreground: "blue"));

        var output = TuiTerminalWriter.Present(previous, next);

        Assert.Contains("\x1b[31m", output, StringComparison.Ordinal);
        Assert.Contains("\x1b[34m", output, StringComparison.Ordinal);
        Assert.EndsWith("\x1b[0m", output, StringComparison.Ordinal);
    }

    [Fact]
    public void A_wide_character_is_written_once_not_twice()
    {
        var previous = Buffer();
        var next = Buffer();
        next.DrawText(0, 0, "日", TuiStyle.Default);

        var output = TuiTerminalWriter.Present(previous, next);

        // The continuation cell contributes no text: the terminal advances two columns
        // on its own when it draws a wide character.
        Assert.Equal("\\e[1;1H日", Readable(output));
    }

    [Fact]
    public void A_frame_with_no_cursor_hides_it()
    {
        var buffer = Buffer();

        Assert.Contains("\x1b[?25l", TuiTerminalWriter.Present(buffer), StringComparison.Ordinal);
    }

    [Fact]
    public void A_frame_with_a_cursor_places_and_shows_it()
    {
        var buffer = Buffer();
        buffer.DrawText(0, 0, "name: bob", TuiStyle.Default);
        buffer.Cursor = (9, 0);

        var output = TuiTerminalWriter.Present(buffer);

        Assert.Contains("\x1b[1;10H", output, StringComparison.Ordinal);
        Assert.Contains("\x1b[?25h", output, StringComparison.Ordinal);
    }

    [Fact]
    public void A_cursor_that_only_moves_is_repositioned_without_being_reshown()
    {
        var previous = Buffer();
        previous.Cursor = (3, 0);

        var next = Buffer();
        next.Cursor = (4, 0);

        var output = TuiTerminalWriter.Present(previous, next);

        Assert.Equal("\\e[1;5H", Readable(output));
    }
}
