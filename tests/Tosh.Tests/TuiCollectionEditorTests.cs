using Tosh.Tui;
using Tosh.Tui.Rendering;
using Tosh.Tui.Widgets;

namespace Tosh.Tests;

/// <summary>
/// A list you can add to, edit and remove from — <c>TUI-0002</c>.
/// </summary>
/// <remarks>
/// <see cref="TuiCollectionEditorState{TItem}"/> tracks the cursor, the input mode and the
/// text being typed, and does not draw. The widget is the drawing half, and it applies the
/// edit itself rather than reporting it — the config browser stages changes against a
/// schema, which is why the state leaves mutation to its owner, but a script asking for a
/// list of tags should not have to implement add, replace and remove first.
/// </remarks>
public sealed class TuiCollectionEditorTests
{
    private const int Width = 30;
    private const int Height = 6;

    private static TuiCollectionEditor Laid(TuiCollectionEditor editor)
    {
        editor.Measure(new TuiConstraints(Width, Height));
        editor.Arrange(new TuiRect(0, 0, Width, Height));

        return editor;
    }

    private static string Render(TuiCollectionEditor editor)
    {
        var buffer = new TuiBuffer(new TuiSize(Width, Height));

        Laid(editor).Draw(new TuiSurface(buffer, new TuiRect(0, 0, Width, Height)));

        return string.Join('\n', Enumerable.Range(0, Height).Select(row => buffer.RowText(row).TrimEnd()));
    }

    private static bool Press(TuiCollectionEditor editor, ConsoleKey key, char ch = '\0')
        => editor.OnInput(TuiInputEvent.FromKey(new ConsoleKeyInfo(ch, key, false, false, false)));

    private static void Type(TuiCollectionEditor editor, string text)
    {
        foreach (var ch in text)
        {
            Press(editor, ConsoleKey.A, ch);
        }
    }

    [Fact]
    public void It_draws_its_entries_with_the_cursor()
    {
        var text = Render(new TuiCollectionEditor(["alpha", "beta"]));

        Assert.Contains("> alpha", text, StringComparison.Ordinal);
        Assert.Contains("  beta", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_arrows_move_the_cursor()
    {
        var editor = Laid(new TuiCollectionEditor(["alpha", "beta"]));

        Assert.True(Press(editor, ConsoleKey.DownArrow));
        Assert.Contains("> beta", Render(editor), StringComparison.Ordinal);
    }

    /// <summary>`n` opens an input line rather than adding an empty entry.</summary>
    [Fact]
    public void Adding_opens_an_input_line()
    {
        var editor = Laid(new TuiCollectionEditor(["alpha"]));

        Assert.True(Press(editor, ConsoleKey.N));
        Assert.True(editor.IsEditing);

        // On the row after the entries, inside the widget's own bounds — not appended past
        // whatever frame is around it.
        var rows = Render(editor).Split('\n');

        Assert.StartsWith("> alpha", rows[0], StringComparison.Ordinal);
        Assert.StartsWith("+ ", rows[1], StringComparison.Ordinal);
    }

    /// <summary>
    /// The widget applies the edit. A script asking for a list of tags should get a list of
    /// tags back, not a notification that someone typed something.
    /// </summary>
    [Fact]
    public void Submitting_an_addition_appends_it_and_reports_the_whole_list()
    {
        IReadOnlyList<object?>? reported = null;
        var editor = Laid(new TuiCollectionEditor(["alpha"]) { Changed = items => reported = items });

        Press(editor, ConsoleKey.N);
        Type(editor, "beta");
        Press(editor, ConsoleKey.Enter);

        Assert.Equal(["alpha", "beta"], editor.Items);
        Assert.Equal(["alpha", "beta"], reported);
        Assert.False(editor.IsEditing);
    }

    [Fact]
    public void Editing_replaces_the_entry_under_the_cursor()
    {
        var editor = Laid(new TuiCollectionEditor(["alpha", "beta"]));

        Press(editor, ConsoleKey.DownArrow);
        Press(editor, ConsoleKey.Enter);
        Assert.True(editor.IsEditing);

        Press(editor, ConsoleKey.Backspace);
        Press(editor, ConsoleKey.Backspace);
        Press(editor, ConsoleKey.Backspace);
        Press(editor, ConsoleKey.Backspace);
        Type(editor, "gamma");
        Press(editor, ConsoleKey.Enter);

        Assert.Equal(["alpha", "gamma"], editor.Items);
    }

    [Theory]
    [InlineData(ConsoleKey.Delete)]
    [InlineData(ConsoleKey.R)]
    public void Removing_takes_the_entry_out(ConsoleKey key)
    {
        IReadOnlyList<object?>? reported = null;
        var editor = Laid(new TuiCollectionEditor(["alpha", "beta"]) { Changed = items => reported = items });

        Assert.True(Press(editor, key));
        Assert.Equal(["beta"], editor.Items);
        Assert.Equal(["beta"], reported);
    }

    /// <summary>While typing, Esc abandons the line rather than the editor.</summary>
    [Fact]
    public void Escape_while_typing_abandons_only_the_line()
    {
        var closed = false;
        var editor = Laid(new TuiCollectionEditor(["alpha"]) { Closed = () => closed = true });

        Press(editor, ConsoleKey.N);
        Type(editor, "beta");
        Assert.True(Press(editor, ConsoleKey.Escape));

        Assert.False(editor.IsEditing);
        Assert.False(closed);
        Assert.Equal(["alpha"], editor.Items);
    }

    /// <summary>And with nothing being typed, it closes the editor.</summary>
    [Fact]
    public void Escape_closes_the_editor()
    {
        var closed = false;
        var editor = Laid(new TuiCollectionEditor(["alpha"]) { Closed = () => closed = true });

        Assert.True(Press(editor, ConsoleKey.Escape));
        Assert.True(closed);
    }

    /// <summary>Removing from an empty list is handled rather than passed on or thrown.</summary>
    [Fact]
    public void Removing_from_an_empty_list_does_nothing()
    {
        var editor = Laid(new TuiCollectionEditor());

        Assert.True(Press(editor, ConsoleKey.Delete));
        Assert.Empty(editor.Items);
    }

    /// <summary>A key with nothing to do here is left for whatever else wants it.</summary>
    [Fact]
    public void An_unrelated_key_is_not_swallowed()
        => Assert.False(Press(Laid(new TuiCollectionEditor(["alpha"])), ConsoleKey.F5));
}
