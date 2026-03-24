using Tosh.Cli;

namespace Tosh.Tests;

public sealed class ReplLineEditorTests
{
    [Fact]
    public void Line_editor_buffer_supports_insertion_cursor_movement_and_deletion()
    {
        var buffer = new LineEditorBuffer("helo");

        Assert.True(buffer.MoveLeft());
        Assert.True(buffer.Insert('l'));
        Assert.Equal("hello", buffer.Text);
        Assert.Equal(4, buffer.CursorIndex);

        Assert.True(buffer.MoveHome());
        Assert.True(buffer.MoveRight());
        Assert.True(buffer.Delete());
        Assert.Equal("hllo", buffer.Text);

        Assert.True(buffer.MoveEnd());
        Assert.True(buffer.Backspace());
        Assert.Equal("hll", buffer.Text);
        Assert.Equal(3, buffer.CursorIndex);
    }

    [Fact]
    public void Line_editor_buffer_handles_home_end_and_clear()
    {
        var buffer = new LineEditorBuffer("toasted");

        Assert.True(buffer.MoveHome());
        Assert.Equal(0, buffer.CursorIndex);

        Assert.True(buffer.MoveEnd());
        Assert.Equal(7, buffer.CursorIndex);

        Assert.True(buffer.Clear());
        Assert.Equal(string.Empty, buffer.Text);
        Assert.Equal(0, buffer.CursorIndex);
    }

    [Fact]
    public void History_navigation_moves_backward_and_restores_pending_input()
    {
        var history = new LineEditorHistory(new[] { "help", "ls -la", "history" });

        Assert.True(history.TryPrevious("par", out var previous1));
        Assert.Equal("history", previous1);

        Assert.True(history.TryPrevious(previous1, out var previous2));
        Assert.Equal("ls -la", previous2);

        Assert.True(history.TryNext(out var next1));
        Assert.Equal("history", next1);

        Assert.True(history.TryNext(out var next2));
        Assert.Equal("par", next2);

        Assert.False(history.TryNext(out var next3));
        Assert.Equal("par", next3);
    }

    [Fact]
    public void History_navigation_stops_at_oldest_entry()
    {
        var history = new LineEditorHistory(new[] { "one", "two" });

        Assert.True(history.TryPrevious(string.Empty, out var first));
        Assert.Equal("two", first);

        Assert.True(history.TryPrevious(first, out var second));
        Assert.Equal("one", second);

        Assert.True(history.TryPrevious(second, out var stillOldest));
        Assert.Equal("one", stillOldest);
    }
}
