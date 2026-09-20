using Tosh.Tui.Rendering;

namespace Tosh.Tui.Widgets;

/// <summary>A path you can type, or browse for.</summary>
/// <remarks>
/// <para>
/// `TUI-0002`. <see cref="TuiPathEditorState"/> is a text input and a file picker sharing one
/// key handler: typing edits the path, a key opens the browser, and choosing in the browser
/// puts the chosen path back in the text. It reads the keys and does not draw.
/// </para>
/// <para>
/// The browse key is <c>F2</c> here rather than the state's default <c>b</c>. That key is
/// checked before the text input, so whatever it is cannot be typed into the path — the
/// config browser trades that away because it offers a separate raw-text editor for the
/// paths that contain a <c>b</c>, and a widget standing on its own has no such second
/// editor. A path field that cannot spell <c>/usr/bin</c> is not a path field.
/// </para>
/// </remarks>
public sealed class TuiPathEditor : TuiWidget
{
    private readonly TuiPathEditorState _state = new();

    public TuiPathEditor(string? text = null)
    {
        _state.BrowseKey = ConsoleKey.F2;
        _state.Open(text ?? string.Empty);
    }

    /// <summary>The path as it currently reads.</summary>
    public string Text
    {
        get => _state.Text;
        set => _state.SetText(value ?? string.Empty);
    }

    /// <summary>Where browsing starts, when it is not the path already typed.</summary>
    public string? BrowseDirectory { get; set; }

    /// <summary>Whether a file, a directory, or either may be chosen while browsing.</summary>
    public TuiFilePickerSelectionMode Mode { get; set; } = TuiFilePickerSelectionMode.Any;

    /// <summary>
    /// The key that opens the browser. <c>F2</c> by default, and never a character.
    /// </summary>
    public ConsoleKey BrowseKey
    {
        get => _state.BrowseKey;
        set => _state.BrowseKey = value;
    }

    /// <summary>Whether the reader is browsing rather than typing.</summary>
    public bool IsBrowsing => _state.IsBrowsing;

    /// <summary>Raised as the text changes, whether typed or chosen.</summary>
    public Action<string>? Changed { get; set; }

    /// <summary>Raised with the path when <c>Enter</c> settles it.</summary>
    public Action<string>? Submitted { get; set; }

    /// <summary>Raised when <c>Esc</c> abandons the edit — not when it closes the browser.</summary>
    public Action? Cancelled { get; set; }

    /// <summary>Whether settling or abandoning ends the screen. Off by default.</summary>
    public bool ClosesOnAnswer { get; set; }

    public TuiStyle Style { get; set; }

    public override bool IsFocusable => true;

    protected override TuiSize MeasureCore(TuiConstraints constraints)
        => constraints.Constrain(
            IsBrowsing
                ? new TuiSize(constraints.MaxWidth <= 0 ? 60 : constraints.MaxWidth,
                              constraints.MaxHeight <= 0 ? 16 : constraints.MaxHeight)
                : new TuiSize(Math.Max(20, TextMeasure.MeasureWidth(Text) + 2), 1));

    public override void Draw(TuiSurface surface)
    {
        if (!IsBrowsing)
        {
            surface.DrawText(0, 0, _state.RenderInputWithCursor(), Style);
            return;
        }

        var lines = _state.BuildPickerEntries(surface.Width, surface.Height);

        for (var row = 0; row < lines.Count && row < surface.Height; row++)
        {
            surface.DrawText(0, row, lines[row], Style);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// While browsing, the keys are the picker's; otherwise they are the text input's. The
    /// page size comes from the bounds for the same reason it does in
    /// <see cref="TuiFilePicker"/>: the state pages by whatever it is told.
    /// </remarks>
    public override bool OnInput(TuiInputEvent input)
    {
        if (!input.IsKey)
        {
            return false;
        }

        var action = _state.HandleKey(input.Key, Math.Max(1, Bounds.Height - 7));

        switch (action.Kind)
        {
            case TuiPathEditorActionKind.BrowseRequested:
                _state.OpenPicker(StartDirectory(), Mode, Text, Math.Max(1, Bounds.Height - 7));
                return true;

            case TuiPathEditorActionKind.PickedPath:
                // The chosen path becomes the text, which is the whole point of having both:
                // browse when you do not know it, type when you do.
                _state.SetText(action.Path ?? string.Empty);
                Changed?.Invoke(Text);
                return true;

            case TuiPathEditorActionKind.PickerClosed:
                // Leaving the browser is not abandoning the edit — the text is still there.
                return true;

            case TuiPathEditorActionKind.TextChanged:
                Changed?.Invoke(action.Text ?? string.Empty);
                return true;

            case TuiPathEditorActionKind.SubmitText:
                Submitted?.Invoke(action.Text ?? string.Empty);
                Answered();
                return true;

            case TuiPathEditorActionKind.Cancel:
                Cancelled?.Invoke();
                Answered();
                return true;

            default:
                return false;
        }
    }

    /// <summary>Where a browse starts: what was asked for, what is typed, or the cwd.</summary>
    private string StartDirectory()
    {
        if (BrowseDirectory is { Length: > 0 } asked)
        {
            return asked;
        }

        if (Text.Length > 0)
        {
            var directory = System.IO.Directory.Exists(Text) ? Text : Path.GetDirectoryName(Text);

            if (!string.IsNullOrEmpty(directory) && System.IO.Directory.Exists(directory))
            {
                return directory;
            }
        }

        return Environment.CurrentDirectory;
    }

    private void Answered()
    {
        if (ClosesOnAnswer)
        {
            ClosesScreen = true;
        }
    }
}
