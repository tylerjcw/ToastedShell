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

    public TuiStyle Style { get; set; }

    public TuiStyle PlaceholderStyle { get; set; } = new(Attributes: TuiTextAttributes.Dim);

    /// <summary>Raised when the text changes.</summary>
    public Action<string>? Changed { get; set; }

    /// <summary>Raised when Enter is pressed.</summary>
    public Action<string>? Submitted { get; set; }

    /// <summary>Raised when Escape is pressed.</summary>
    public Action? Cancelled { get; set; }

    public override bool IsFocusable => true;

    public override TuiSize Measure(TuiConstraints constraints)
        => constraints.Constrain(new TuiSize(
            Math.Max(TuiTextMeasure.MeasureWidth(Text) + 1, TuiTextMeasure.MeasureWidth(Placeholder ?? string.Empty)),
            1));

    public override void Draw(TuiSurface surface)
    {
        if (Text.Length == 0 && Placeholder is { Length: > 0 } placeholder && !IsFocused)
        {
            surface.DrawText(0, 0, placeholder, PlaceholderStyle);
            return;
        }

        var text = Mask ? new string('•', Text.Length) : Text;
        surface.DrawText(0, 0, text, Style);
    }

    /// <summary>Where the caret belongs, in this field's own coordinates.</summary>
    /// <remarks>
    /// Reported rather than drawn: the terminal draws a better caret than a reversed cell
    /// does, and only the screen knows the absolute position to give it.
    /// </remarks>
    public int CaretColumn => TuiTextMeasure.MeasureWidth(Text[..Math.Min(_state.CursorIndex, Text.Length)]);

    public override bool OnInput(TuiInputEvent input)
    {
        if (!input.IsKey || !IsFocused)
        {
            return false;
        }

        var before = Text;

        switch (_state.HandleKey(input.Key))
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
