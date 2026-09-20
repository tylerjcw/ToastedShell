using Tosh.Tui.Rendering;

namespace Tosh.Tui.Widgets;

/// <summary>A filesystem browser that returns the path the reader chose.</summary>
/// <remarks>
/// <para>
/// `TUI-0002`. <see cref="TuiFilePickerState"/> walks directories, filters by what is being
/// asked for, keeps the cursor and the scroll, and builds the lines — and does not draw.
/// Drawing belonged to <c>TuiFilePickerScreen</c>, which is a whole screen: usable from the
/// shell, unusable as one field on somebody else's form.
/// </para>
/// <para>
/// The state already assembles its own display through <c>BuildEntries</c>, so this is
/// mostly wiring. Two details are not: the state sizes its page from the height it is given
/// and its key handling takes a page size, so both have to agree with the bounds this widget
/// was actually arranged into, or paging moves by a different amount than the reader sees.
/// </para>
/// </remarks>
public sealed class TuiFilePicker : TuiWidget
{
    private readonly TuiFilePickerState _state = new();
    private string _directory = string.Empty;
    private TuiFilePickerSelectionMode _mode = TuiFilePickerSelectionMode.Any;
    private string? _initialPath;

    public TuiFilePicker(string? directory = null)
    {
        Directory = directory ?? Environment.CurrentDirectory;
    }

    /// <summary>Where the browse starts. Setting it moves there.</summary>
    public string Directory
    {
        get => _state.IsOpen ? _state.CurrentDirectory : _directory;
        set
        {
            _directory = value ?? string.Empty;
            Reopen();
        }
    }

    /// <summary>Whether a file, a directory, or either may be chosen.</summary>
    public TuiFilePickerSelectionMode Mode
    {
        get => _mode;
        set
        {
            _mode = value;
            Reopen();
        }
    }

    /// <summary>A path to start the cursor on, if it is in the starting directory.</summary>
    public string? InitialPath
    {
        get => _initialPath;
        set
        {
            _initialPath = value;
            Reopen();
        }
    }

    /// <summary>Raised with the chosen path.</summary>
    public Action<string>? Selected { get; set; }

    /// <summary>Raised when <c>Esc</c> abandons the browse.</summary>
    public Action? Cancelled { get; set; }

    /// <summary>
    /// Whether choosing or abandoning ends the screen. On by default.
    /// </summary>
    /// <remarks>
    /// Like <see cref="TuiConfirm"/> and unlike the editors: a picker is asked a question and
    /// answers it. One that stayed up after a choice would leave the reader browsing a
    /// filesystem they had already finished with.
    /// </remarks>
    public bool ClosesOnAnswer { get; set; } = true;

    public TuiStyle Style { get; set; }

    public override bool IsFocusable => true;

    /// <summary>What the reader is looking at, for a caller that wants to show it.</summary>
    public string CurrentDirectory => _state.CurrentDirectory;

    protected override TuiSize MeasureCore(TuiConstraints constraints)
        => constraints.Constrain(new TuiSize(
            constraints.MaxWidth <= 0 ? 60 : constraints.MaxWidth,
            constraints.MaxHeight <= 0 ? 16 : constraints.MaxHeight));

    public override void Draw(TuiSurface surface)
    {
        var lines = _state.BuildEntries(surface.Width, surface.Height);

        for (var row = 0; row < lines.Count && row < surface.Height; row++)
        {
            surface.DrawText(0, row, lines[row], Style);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// The page size is taken from the bounds rather than guessed, because the state pages by
    /// whatever it is told and builds its own view from the height it was drawn at — tell it
    /// two different numbers and <c>PageDown</c> moves by a different amount than the reader
    /// can see.
    /// </remarks>
    public override bool OnInput(TuiInputEvent input)
    {
        if (!input.IsKey)
        {
            return false;
        }

        var result = _state.HandleKey(input.Key, PageSize);

        switch (result.Kind)
        {
            case TuiFilePickerResultKind.Selected:
                Selected?.Invoke(result.Path ?? string.Empty);
                Answered();
                return true;

            case TuiFilePickerResultKind.Cancelled:
                Cancelled?.Invoke();
                Answered();
                return true;

            default:
                // Navigation is the picker's, whether or not it moved: letting an arrow past
                // would move whatever is behind a picker that is already at its last entry.
                return Navigates(input.Key);
        }
    }

    /// <summary>The rows the state shows, which is the height less its own furniture.</summary>
    private int PageSize => Math.Max(1, Bounds.Height - 7);

    private void Answered()
    {
        if (ClosesOnAnswer)
        {
            ClosesScreen = true;
        }
    }

    private static bool Navigates(ConsoleKeyInfo key)
        => key.Key is ConsoleKey.UpArrow or ConsoleKey.DownArrow or ConsoleKey.PageUp
            or ConsoleKey.PageDown or ConsoleKey.Home or ConsoleKey.End or ConsoleKey.LeftArrow
            or ConsoleKey.RightArrow or ConsoleKey.Backspace;

    private void Reopen()
        => _state.Open(
            string.IsNullOrEmpty(_directory) ? Environment.CurrentDirectory : _directory,
            _mode,
            _initialPath,
            Math.Max(1, Bounds.Height - 7));
}
