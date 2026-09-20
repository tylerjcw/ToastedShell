using Tosh.Tui;
using Tosh.Tui.Rendering;
using Tosh.Tui.Widgets;

namespace Tosh.Tests;

/// <summary>
/// A path you can type, or browse for — <c>TUI-0002</c>.
/// </summary>
/// <remarks>
/// <see cref="TuiPathEditorState"/> is a text input and a file picker sharing one key
/// handler, and does not draw. The browse key is checked before the text input, so whatever
/// it is cannot be typed into the path — which is why this widget uses <c>F2</c> and not the
/// state's default <c>b</c>. The config browser can afford <c>b</c> because it offers a
/// separate raw-text editor; a widget standing alone cannot.
/// </remarks>
public sealed class TuiPathEditorWidgetTests : IDisposable
{
    private const int Width = 50;
    private const int Height = 16;

    private readonly string _root = Directory.CreateTempSubdirectory("tui-path-").FullName;

    public TuiPathEditorWidgetTests() => File.WriteAllText(Path.Combine(_root, "alpha.txt"), "a");

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static TuiPathEditor Laid(TuiPathEditor editor)
    {
        editor.Measure(new TuiConstraints(Width, Height));
        editor.Arrange(new TuiRect(0, 0, Width, Height));

        return editor;
    }

    private static string Render(TuiPathEditor editor)
    {
        var buffer = new TuiBuffer(new TuiSize(Width, Height));

        Laid(editor).Draw(new TuiSurface(buffer, new TuiRect(0, 0, Width, Height)));

        return string.Join('\n', Enumerable.Range(0, Height).Select(row => buffer.RowText(row).TrimEnd()));
    }

    private static bool Press(TuiPathEditor editor, ConsoleKey key, char ch = '\0')
        => editor.OnInput(TuiInputEvent.FromKey(new ConsoleKeyInfo(ch, key, false, false, false)));

    private static void Type(TuiPathEditor editor, string text)
    {
        foreach (var ch in text)
        {
            Press(editor, ConsoleKey.A, ch);
        }
    }

    [Fact]
    public void It_draws_the_path_being_typed()
        => Assert.Contains("/etc/hosts", Render(new TuiPathEditor("/etc/hosts")), StringComparison.Ordinal);

    /// <summary>
    /// The point of not using a letter. A path field that cannot spell `/usr/bin` is not a
    /// path field, and `b` is checked before the text input.
    /// </summary>
    [Fact]
    public void A_path_containing_b_can_be_typed()
    {
        var editor = Laid(new TuiPathEditor());

        Type(editor, "/usr/bin");

        Assert.Equal("/usr/bin", editor.Text);
        Assert.False(editor.IsBrowsing);
    }

    /// <summary>And the state's own default still browses, for the config browser.</summary>
    [Fact]
    public void The_state_still_browses_on_b_by_default()
    {
        var state = new TuiPathEditorState();
        state.Open(string.Empty);

        var action = state.HandleKey(new ConsoleKeyInfo('b', ConsoleKey.B, false, false, false), 8);

        Assert.Equal(TuiPathEditorActionKind.BrowseRequested, action.Kind);
    }

    [Fact]
    public void F2_opens_the_browser()
    {
        var editor = Laid(new TuiPathEditor { BrowseDirectory = _root });

        Assert.True(Press(editor, ConsoleKey.F2));
        Assert.True(editor.IsBrowsing);
        Assert.Contains("alpha.txt", Render(editor), StringComparison.Ordinal);
    }

    /// <summary>Choosing puts the path into the text: browse when you do not know it.</summary>
    [Fact]
    public void Choosing_in_the_browser_becomes_the_text()
    {
        string? changed = null;
        var editor = Laid(new TuiPathEditor { BrowseDirectory = _root, Changed = text => changed = text });

        Press(editor, ConsoleKey.F2);
        Press(editor, ConsoleKey.Spacebar);

        Assert.False(editor.IsBrowsing);
        Assert.StartsWith(_root, editor.Text, StringComparison.Ordinal);
        Assert.Equal(editor.Text, changed);
    }

    /// <summary>Leaving the browser is not abandoning the edit — the text is still there.</summary>
    [Fact]
    public void Escape_while_browsing_keeps_the_text()
    {
        var cancelled = false;
        var editor = Laid(new TuiPathEditor("/etc/hosts")
        {
            BrowseDirectory = _root,
            Cancelled = () => cancelled = true,
        });

        Press(editor, ConsoleKey.F2);
        Assert.True(Press(editor, ConsoleKey.Escape));

        Assert.False(editor.IsBrowsing);
        Assert.False(cancelled);
        Assert.Equal("/etc/hosts", editor.Text);
    }

    /// <summary>And with no browser up, Esc abandons the edit.</summary>
    [Fact]
    public void Escape_while_typing_abandons_the_edit()
    {
        var cancelled = false;
        var editor = Laid(new TuiPathEditor("/etc") { Cancelled = () => cancelled = true });

        Assert.True(Press(editor, ConsoleKey.Escape));
        Assert.True(cancelled);
    }

    [Fact]
    public void Enter_settles_the_path()
    {
        string? submitted = null;
        var editor = Laid(new TuiPathEditor("/etc") { Submitted = text => submitted = text });

        Assert.True(Press(editor, ConsoleKey.Enter));
        Assert.Equal("/etc", submitted);
    }

    /// <summary>Typing reports as it goes, so a caller can preview what a path resolves to.</summary>
    [Fact]
    public void Typing_reports_each_change()
    {
        var seen = new List<string>();
        var editor = Laid(new TuiPathEditor { Changed = text => seen.Add(text) });

        Type(editor, "ab");

        Assert.Equal(["a", "ab"], seen);
    }
}
