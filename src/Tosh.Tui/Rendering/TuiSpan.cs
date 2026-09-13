namespace Tosh.Tui.Rendering;

/// <summary>A run of text drawn in one style.</summary>
/// <remarks>
/// <para>
/// A row is rarely one style all the way across. A sidebar entry is a selection marker,
/// an indent, a name and a dimmed count; a help page's line is a heading or a parameter or
/// prose. Both browsers already build rows this way — as a sequence of
/// <c>(text, style)</c> pairs handed to a renderer — and this is that pair, in the terms
/// the widget layer draws in.
/// </para>
/// <para>
/// Nothing here knows about wrapping. A span is a run within a line; where the line breaks
/// is the business of whatever laid the line out.
/// </para>
/// </remarks>
public readonly record struct TuiSpan(string Text, TuiStyle Style = default)
{
    /// <summary>How many columns this run occupies.</summary>
    public int Width => TuiTextMeasure.MeasureWidth(Text);

    public static implicit operator TuiSpan(string text) => new(text);
}

/// <summary>A row of styled runs.</summary>
public sealed class TuiSpanLine
{
    private readonly List<TuiSpan> _spans = [];

    public TuiSpanLine()
    {
    }

    public TuiSpanLine(IEnumerable<TuiSpan> spans)
    {
        ArgumentNullException.ThrowIfNull(spans);
        _spans.AddRange(spans);
    }

    public IReadOnlyList<TuiSpan> Spans => _spans;

    /// <summary>How many columns the whole row occupies.</summary>
    public int Width => _spans.Sum(span => span.Width);

    /// <summary>The row as plain text, for a caller that only wants to read it.</summary>
    public string Text => string.Concat(_spans.Select(span => span.Text));

    /// <summary>Appends a run. Returns this line, so a row can be written as a chain.</summary>
    public TuiSpanLine Add(string text, TuiStyle style = default)
    {
        if (!string.IsNullOrEmpty(text))
        {
            _spans.Add(new TuiSpan(text, style));
        }

        return this;
    }

    /// <summary>Appends a run.</summary>
    public TuiSpanLine Add(TuiSpan span) => Add(span.Text, span.Style);

    /// <summary>Draws the row at <paramref name="row"/>, clipped to the surface.</summary>
    public void Draw(TuiSurface surface, int row, int column = 0)
    {
        foreach (var span in _spans)
        {
            if (column >= surface.Width)
            {
                return;
            }

            column += surface.DrawText(column, row, span.Text, span.Style, surface.Width - column);
        }
    }
}
