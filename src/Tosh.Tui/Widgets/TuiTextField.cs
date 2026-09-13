using Tosh.Tui.Rendering;

namespace Tosh.Tui.Widgets;

/// <summary>A single-line text field.</summary>
/// <remarks>
/// <para>
/// The widget that makes <c>TOSH-0011</c> unrepeatable. While it holds focus it consumes
/// every printable character, so a screen-level shortcut never sees a letter someone is
/// typing — and the screen does not have to know a text field exists in order for that to
/// be true.
/// </para>
/// <para>
/// It wraps <see cref="TuiTextInputState"/>, which is one of the state types that had no
/// drawing half, and gives it one.
/// </para>
/// </remarks>
public sealed class TuiTextField : TuiWidget
{
    private readonly TuiTextInputState _state = new();

    public TuiTextField(string text = "")
    {
        _state.SetText(text);
    }

    /// <summary>The current text.</summary>
    public string Text
    {
        get => _state.Text;
        set => _state.SetText(value);
    }

    /// <summary>Shown in place of the text when it is empty.</summary>
    public string? Placeholder { get; set; }

    /// <summary>Draws the characters as dots.</summary>
    public bool Mask { get; set; }

    /// <summary>
    /// Accepts newlines, growing downwards, with submission moved to Ctrl+Enter.
    /// </summary>
    public bool Multiline { get; set; }

    public TuiStyle Style { get; set; }

    public TuiStyle PlaceholderStyle { get; set; } = new(Attributes: TuiTextAttributes.Dim);

    /// <summary>Raised when the text changes.</summary>
    public Action<string>? Changed { get; set; }

    /// <summary>Raised when Enter is pressed.</summary>
    public Action<string>? Submitted { get; set; }

    /// <summary>Raised when Escape is pressed.</summary>
    public Action? Cancelled { get; set; }

    /// <inheritdoc />
    public override object? Value => Text;

    public override bool IsFocusable => true;

    private string[] Lines => (Mask ? new string('•', Text.Length) : Text).Split('\n');

    public override TuiSize Measure(TuiConstraints constraints)
    {
        var lines = Lines;

        // One column wider than the text, so there is somewhere for the caret to sit at
        // the end of the longest line.
        var width = Math.Max(
            lines.Max(TuiTextMeasure.MeasureWidth) + 1,
            TuiTextMeasure.MeasureWidth(Placeholder ?? string.Empty));

        return constraints.Constrain(new TuiSize(width, Multiline ? lines.Length : 1));
    }

    public override void Draw(TuiSurface surface)
    {
        if (Text.Length == 0 && Placeholder is { Length: > 0 } placeholder && !IsFocused)
        {
            surface.DrawText(0, 0, placeholder, PlaceholderStyle);
            return;
        }

        var lines = Lines;

        for (var row = 0; row < lines.Length && row < surface.Height; row += 1)
        {
            surface.DrawText(0, row, lines[row], Style);
        }
    }

    /// <summary>Where the caret belongs, in this field's own coordinates.</summary>
    /// <remarks>
    /// Reported rather than drawn: the terminal draws a better caret than a reversed cell
    /// does, and only the screen knows the absolute position to give it.
    /// </remarks>
    public int CaretColumn
    {
        get
        {
            var before = Text[..Math.Min(_state.CursorIndex, Text.Length)];
            var lastBreak = before.LastIndexOf('\n');

            return TuiTextMeasure.MeasureWidth(lastBreak < 0 ? before : before[(lastBreak + 1)..]);
        }
    }

    /// <summary>Which line the caret is on, for a multiline field.</summary>
    public int CaretRow => Text[..Math.Min(_state.CursorIndex, Text.Length)].Count(character => character == '\n');

    /// <summary>The offset in the text at a position within this field.</summary>
    private int IndexAt(int row, int column)
    {
        var lines = Text.Split('\n');
        var index = 0;

        for (var line = 0; line < Math.Min(row, lines.Length - 1); line += 1)
        {
            index += lines[line].Length + 1;
        }

        var target = lines[Math.Clamp(row, 0, lines.Length - 1)];

        return index + Math.Clamp(column, 0, target.Length);
    }

    public override bool OnInput(TuiInputEvent input)
    {
        if (!input.IsKey)
        {
            var mouse = input.Mouse;

            // A click puts the caret where it was clicked, which is the one thing a
            // mouse is unambiguously for in a text field.
            if (mouse.Action != TuiMouseAction.Press ||
                mouse.Button != TuiMouseButton.Left ||
                !Bounds.Contains(mouse.Column, mouse.Row))
            {
                return false;
            }

            _state.SetCursorIndex(IndexAt(mouse.Row - Bounds.Top, mouse.Column - Bounds.Left));
            return true;
        }

        if (!IsFocused)
        {
            return false;
        }

        var before = Text;

        switch (_state.HandleKey(input.Key, Multiline))
        {
            case TuiTextInputResult.Submit:
                Submitted?.Invoke(Text);
                return true;

            case TuiTextInputResult.Cancel:
                Cancelled?.Invoke();
                return true;

            case TuiTextInputResult.Changed:
                if (Text != before)
                {
                    Changed?.Invoke(Text);
                }

                return true;

            default:
                // Tab, function keys and the like belong to whatever is outside.
                return false;
        }
    }
}
