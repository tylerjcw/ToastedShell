using Tosh.Runtime;
using Tosh.Tui.Rendering;

namespace Tosh.Tui.Widgets;

/// <summary>A caption and an input on one row.</summary>
/// <remarks>
/// <para>
/// A form row, as one widget. Assembling it by hand — a horizontal stack, a text widget
/// sized to its content, a field taking the rest — is four lines and the same four lines
/// every time, which is the sort of thing a widget set exists to absorb.
/// </para>
/// <para>
/// The caption is not focusable and the input is, so the keyboard lands on the input
/// without anything having to say so. An id given to the field is the id its value is
/// reported under, because the row answers with what its input holds.
/// </para>
/// </remarks>
public sealed class TuiField : TuiWidget
{
    private readonly TuiTextWidget _caption;
    private readonly TuiTextField _input;
    private readonly TuiStack _row;

    public TuiField(string label = "", string text = "")
    {
        _caption = new TuiTextWidget { Style = new TuiStyle(Attributes: TuiTextAttributes.Dim) };
        _input = new TuiTextField(text);

        _row = new TuiStack(TuiOrientation.Horizontal)
            .Add(_caption)
            .Add(_input, TuiLength.Star());

        Label = label;
    }

    /// <summary>The caption shown before the input.</summary>
    public string Label
    {
        get;
        set
        {
            field = value ?? string.Empty;

            // The separator lives here rather than in the caller's string, so every form
            // in every script lines up the same way.
            _caption.Text = field.Length == 0 ? string.Empty : $"{field}:";

            // Measured rather than left to the caption: a text widget reports the width
            // of what it would draw, and trailing blanks are not drawn — so a caption
            // sized to its content loses the gap that separates it from the input.
            _caption.Size = field.Length == 0
                ? TuiLength.Auto
                : TuiLength.Fixed(TuiTextMeasure.MeasureWidth(field) + 2);
        }
    } = string.Empty;

    /// <summary>What the input holds.</summary>
    public string Text
    {
        get => _input.Text;
        set => _input.Text = value;
    }

    /// <summary>A script function supplying the text, re-read on every redraw.</summary>
    public IShellCallable? TextSource
    {
        get => _input.ValueSource;
        set => _input.ValueSource = value;
    }

    /// <summary>Shown when the input is empty.</summary>
    public string? Placeholder
    {
        get => _input.Placeholder;
        set => _input.Placeholder = value;
    }

    /// <summary>Draws the characters as dots.</summary>
    public bool Password
    {
        get => _input.Mask;
        set => _input.Mask = value;
    }

    /// <summary>Accepts newlines, growing downwards.</summary>
    public bool Multiline
    {
        get => _input.Multiline;
        set => _input.Multiline = value;
    }

    /// <summary>Raised when the text changes.</summary>
    public Action<string>? Changed
    {
        get => _input.Changed;
        set => _input.Changed = value;
    }

    /// <summary>Raised when Enter is pressed in the input.</summary>
    public Action<string>? Submitted
    {
        get => _input.Submitted;
        set => _input.Submitted = value;
    }

    /// <summary>The input itself, for a caller that needs it.</summary>
    public TuiTextField Input => _input;

    /// <inheritdoc />
    public override object? Value => _input.Text;

    public override IReadOnlyList<TuiWidget> Children => [_row];

    protected override TuiSize MeasureCore(TuiConstraints constraints) => _row.Measure(constraints);

    protected override void ArrangeCore(TuiRect bounds)
    {
        _row.Arrange(bounds);
    }

    public override void Draw(TuiSurface surface) => DrawChild(_row, surface);
}
