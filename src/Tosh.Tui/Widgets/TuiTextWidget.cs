using Tosh.Runtime;
using Tosh.Tui.Rendering;

namespace Tosh.Tui.Widgets;

/// <summary>Wrapped text.</summary>
/// <remarks>
/// Wrapping keeps the line breaks the author wrote — a block with headings and columns
/// is not a paragraph — and measures its width in columns rather than code units, so a
/// line of CJK does not report half the width it draws.
/// </remarks>
public sealed class TuiTextWidget : TuiWidget
{
    private IReadOnlyList<string> _lines = [];
    private int _wrappedAt = -1;

    public TuiTextWidget(string text = "", TuiStyle style = default)
    {
        Text = text;
        Style = style;
    }

    /// <summary>The text to draw.</summary>
    public string Text
    {
        get;
        set
        {
            ArgumentNullException.ThrowIfNull(value);

            if (field != value)
            {
                field = value;
                _wrappedAt = -1;
            }
        }
    } = string.Empty;

    /// <summary>A script function that supplies the text, re-read on every redraw.</summary>
    /// <remarks>
    /// Assigning one of these is what makes a property live. Setting <see cref="Text"/>
    /// puts a value there once; setting this puts a question there, asked again each time
    /// the screen is drawn.
    /// </remarks>
    public IShellCallable? TextSource { get; set; }

    /// <summary>How the text is drawn.</summary>
    public TuiStyle Style { get; set; }

    /// <summary>Whether to wrap to the available width, or clip at it.</summary>
    public bool Wrap
    {
        get;
        set
        {
            if (field != value)
            {
                field = value;
                _wrappedAt = -1;
            }
        }
    } = true;

    protected override TuiSize MeasureCore(TuiConstraints constraints)
    {
        // One row is a label, whatever the wrap setting says, so it asks for the room its
        // whole line would take. Answering with the wrapped width instead said a path
        // fitted when only its first line did.
        if (constraints.MaxHeight == 1)
        {
            return constraints.Constrain(new TuiSize(TextMeasure.MeasureWidth(FirstLine()), 1));
        }

        var lines = LayOut(constraints.MaxWidth);
        var width = lines.Count == 0 ? 0 : lines.Max(TextMeasure.MeasureWidth);

        return constraints.Constrain(new TuiSize(width, lines.Count));
    }

    /// <summary>The first line as written, before any wrapping.</summary>
    private string FirstLine()
    {
        var text = Text.Replace("\r\n", "\n");
        var breakAt = text.IndexOf('\n', StringComparison.Ordinal);

        return breakAt < 0 ? text : text[..breakAt];
    }

    public override void Draw(TuiSurface surface)
    {
        if (surface.Height == 1)
        {
            DrawLabel(surface);
            return;
        }

        var lines = LayOut(surface.Width);

        for (var row = 0; row < lines.Count && row < surface.Height; row += 1)
        {
            // Wrapped text that does not fit is folded, not cut, so there is nothing to
            // mark. Unwrapped text that does not fit is cut, and a cut with no mark reads
            // as text that happened to end there.
            surface.DrawText(
                0,
                row,
                Wrap ? lines[row] : TextMeasure.Elide(lines[row], surface.Width),
                Style);
        }
    }

    /// <summary>Draws a label: one line, cut rather than folded, and marked where it cut.</summary>
    /// <remarks>
    /// Folding onto rows that will never be drawn is how a status bar shows the first
    /// thirty-eight characters of a path and stops, with nothing to say the rest exists.
    /// </remarks>
    private void DrawLabel(TuiSurface surface)
    {
        surface.DrawText(0, 0, TextMeasure.Elide(FirstLine(), surface.Width), Style);
    }

    /// <summary>
    /// Wraps once per width rather than once per call.
    /// </summary>
    /// <remarks>
    /// Measure and Draw both need the laid-out lines, and a redrawing screen asks for
    /// them every frame at a width that rarely changes.
    /// </remarks>
    private IReadOnlyList<string> LayOut(int width)
    {
        var effective = Math.Max(1, width == int.MaxValue ? TextMeasure.MeasureWidth(Text) + 1 : width);

        if (_wrappedAt == effective)
        {
            return _lines;
        }

        _lines = Wrap
            ? TextDocumentFormatter.WrapDocument(Text, effective)
            : Text.Replace("\r\n", "\n").Split('\n');
        _wrappedAt = effective;

        return _lines;
    }
}
