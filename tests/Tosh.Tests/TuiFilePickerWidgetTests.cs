using Tosh.Tui;
using Tosh.Tui.Rendering;
using Tosh.Tui.Widgets;

namespace Tosh.Tests;

/// <summary>
/// A filesystem browser as a widget — <c>TUI-0002</c>.
/// </summary>
/// <remarks>
/// <see cref="TuiFilePickerState"/> walks directories, filters by what is being asked for,
/// and builds its own lines — and does not draw. Drawing belonged to
/// <c>TuiFilePickerScreen</c>, which is a whole screen: usable from the shell, unusable as
/// one field on somebody else's form.
/// </remarks>
public sealed class TuiFilePickerWidgetTests : IDisposable
{
    private const int Width = 60;
    private const int Height = 16;

    private readonly string _root = System.IO.Directory.CreateTempSubdirectory("tui-picker-").FullName;

    public TuiFilePickerWidgetTests()
    {
        System.IO.Directory.CreateDirectory(Path.Combine(_root, "inner"));
        File.WriteAllText(Path.Combine(_root, "alpha.txt"), "a");
        File.WriteAllText(Path.Combine(_root, "beta.txt"), "b");
    }

    public void Dispose() => System.IO.Directory.Delete(_root, recursive: true);

    private TuiFilePicker Laid(TuiFilePicker picker)
    {
        picker.Measure(new TuiConstraints(Width, Height));
        picker.Arrange(new TuiRect(0, 0, Width, Height));

        return picker;
    }

    private TuiFilePicker Picker(TuiFilePickerSelectionMode mode = TuiFilePickerSelectionMode.Any)
        => Laid(new TuiFilePicker(_root) { Mode = mode });

    private string Render(TuiFilePicker picker)
    {
        var buffer = new TuiBuffer(new TuiSize(Width, Height));

        Laid(picker).Draw(new TuiSurface(buffer, new TuiRect(0, 0, Width, Height)));

        return string.Join('\n', Enumerable.Range(0, Height).Select(row => buffer.RowText(row).TrimEnd()));
    }

    private static bool Press(TuiFilePicker picker, ConsoleKey key)
        => picker.OnInput(TuiInputEvent.FromKey(new ConsoleKeyInfo('\0', key, false, false, false)));

    [Fact]
    public void It_draws_where_it_is_and_what_is_there()
    {
        var text = Render(Picker());

        Assert.Contains("Location:", text, StringComparison.Ordinal);
        Assert.Contains("alpha.txt", text, StringComparison.Ordinal);
        Assert.Contains("inner", text, StringComparison.Ordinal);
    }

    /// <summary>The mode decides what may be chosen, not what is shown.</summary>
    [Fact]
    public void A_file_picker_still_walks_directories()
    {
        var text = Render(Picker(TuiFilePickerSelectionMode.File));

        Assert.Contains("inner", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Space_chooses_the_highlighted_path()
    {
        string? chosen = null;
        var picker = Picker();
        picker.Selected = path => chosen = path;

        Assert.True(Press(picker, ConsoleKey.Spacebar));
        Assert.NotNull(chosen);
        Assert.StartsWith(_root, chosen, StringComparison.Ordinal);
    }

    [Fact]
    public void Escape_abandons_the_browse()
    {
        var cancelled = false;
        var picker = Picker();
        picker.Cancelled = () => cancelled = true;

        Assert.True(Press(picker, ConsoleKey.Escape));
        Assert.True(cancelled);
    }

    /// <summary>Choosing ends the screen, as a question answered should.</summary>
    [Fact]
    public void Answering_ends_the_screen_by_default()
    {
        var picker = Picker();

        Press(picker, ConsoleKey.Spacebar);

        Assert.True(picker.ClosesScreen);
    }

    /// <summary>And an embedded one is left where it is.</summary>
    [Fact]
    public void An_embedded_picker_does_not_end_the_screen()
    {
        var picker = Laid(new TuiFilePicker(_root) { ClosesOnAnswer = false });

        Press(picker, ConsoleKey.Spacebar);

        Assert.False(picker.ClosesScreen);
    }

    /// <summary>Right enters a directory, and the location follows.</summary>
    [Fact]
    public void Right_enters_a_directory()
    {
        var picker = Picker();
        var before = picker.CurrentDirectory;

        // The first entry is the parent link, so step past it to reach `inner`.
        Press(picker, ConsoleKey.DownArrow);
        Press(picker, ConsoleKey.RightArrow);

        Assert.NotEqual(before, picker.CurrentDirectory);
    }

    /// <summary>
    /// Navigation is the picker's whether or not it moved. Letting an arrow past at the last
    /// entry would move whatever is behind it.
    /// </summary>
    [Theory]
    [InlineData(ConsoleKey.UpArrow)]
    [InlineData(ConsoleKey.DownArrow)]
    [InlineData(ConsoleKey.PageDown)]
    [InlineData(ConsoleKey.Home)]
    [InlineData(ConsoleKey.End)]
    public void Navigation_keys_are_answered(ConsoleKey key)
        => Assert.True(Press(Picker(), key));

    /// <summary>A key with nothing to do here is left for whatever else wants it.</summary>
    [Fact]
    public void An_unrelated_key_is_not_swallowed()
        => Assert.False(Press(Picker(), ConsoleKey.F5));
}
