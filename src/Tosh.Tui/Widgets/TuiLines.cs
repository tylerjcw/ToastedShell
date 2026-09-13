using Tosh.Runtime;
using Tosh.Tui.Rendering;

namespace Tosh.Tui.Widgets;

/// <summary>
/// A document of styled lines that scrolls itself.
/// </summary>
/// <remarks>
/// <para>
/// The detail pane of both browsers: a help page, a config node's description, a type's
/// members. Each line is a row of styled runs rather than one style throughout, because
/// that is what a heading followed by prose followed by a dimmed default actually is.
/// </para>
/// <para>
/// It owns its offset, the way <see cref="TuiList"/> owns its selection. Scrolling is a
/// relationship between a viewport and content larger than it, and the widget that has
/// both is the one that can maintain it — which is why every screen that scrolled used to
/// keep a <c>TuiScrollState</c> and drive it by hand, recomputing a page size from a
/// height at each call site (<c>TUI-0007</c>).
/// </para>
/// <para>
/// Not to be confused with <see cref="TuiScroll"/>, which windows onto a child that draws
/// itself at full height. This draws only the rows it shows, so a thousand-line page costs
/// the rows on screen rather than a thousand-row buffer.
/// </para>
/// </remarks>
public sealed class TuiLines : TuiWidget
{
    private IReadOnlyList<TuiSpanLine> _lines = [];
    private int _offset;

    public TuiLines(IEnumerable<TuiSpanLine>? lines = null)
    {
        if (lines is not null)
        {
            Lines = [.. lines];
        }
    }

    /// <summary>The lines to draw.</summary>
    /// <remarks>
    /// Replacing them keeps the offset where it was, clamped — a pane refreshed underneath
    /// a reader should not jump back to the top, and one that got shorter should not be
    /// left pointing past the end.
    /// </remarks>
    public IReadOnlyList<TuiSpanLine> Lines
    {
        get => _lines;
        set
        {
            ArgumentNullException.ThrowIfNull(value);

            _lines = value;
            Offset = _offset;
        }
    }

    /// <summary>A script function supplying the lines, re-read on every redraw.</summary>
    public IShellCallable? LinesSource { get; set; }

    /// <summary>The first line shown.</summary>
    public int Offset
    {
        get => _offset;
        set => _offset = Math.Clamp(value, 0, MaxOffset);
    }

    /// <summary>How far the offset can go before the last line is at the bottom.</summary>
    public int MaxOffset => Math.Max(0, _lines.Count - Math.Max(1, Bounds.Height));

    /// <summary>Draws a bar down the right edge saying where in the document you are.</summary>
    /// <remarks>Off by default: a bar beside content that entirely fits is noise.</remarks>
    public bool Scrollbar { get; set; }

    /// <summary>How the scrollbar is drawn, when there is one.</summary>
    public TuiStyle ScrollbarStyle { get; set; } = new(Attributes: TuiTextAttributes.Dim);

    /// <summary>Whether the keyboard can reach it, which it should when it can scroll.</summary>
    public override bool IsFocusable { get; } = true;

    /// <summary>The whole document as plain text.</summary>
    public override object? Value => string.Join('\n', _lines.Select(line => line.Text));

    /// <summary>Lines a caller can set by hand, as plain text.</summary>
    public string Text
    {
        set => Lines = [.. (value ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .Select(line => new TuiSpanLine([new TuiSpan(line)]))];
    }

    /// <inheritdoc />
    public override TuiSize Measure(TuiConstraints constraints)
        => constraints.Constrain(new TuiSize(
            _lines.Count == 0 ? 0 : _lines.Max(line => line.Width),
            _lines.Count));

    /// <inheritdoc />
    public override void Arrange(TuiRect bounds)
    {
        base.Arrange(bounds);

        // The offset is only meaningful against a height, and the height is only known
        // here. Re-clamping on arrange is what keeps a resized pane in range.
        Offset = _offset;
    }

    /// <inheritdoc />
    public override void Draw(TuiSurface surface)
    {
        var content = Scrollbar && _lines.Count > surface.Height
            ? surface.Clip(new TuiRect(0, 0, Math.Max(0, surface.Width - 1), surface.Height))
            : surface;

        for (var row = 0; row < content.Height; row += 1)
        {
            var index = _offset + row;

            if (index >= _lines.Count)
            {
                break;
            }

            _lines[index].Draw(content, row);
        }

        if (Scrollbar)
        {
            TuiScrollbar.DrawVertical(surface, _offset, _lines.Count, ScrollbarStyle, ScrollbarStyle);
        }
    }

    /// <inheritdoc />
    public override bool OnInput(TuiInputEvent input)
    {
        if (!input.IsKey)
        {
            return ScrollWheel(input.Mouse);
        }

        if (!IsFocused)
        {
            return false;
        }

        var page = Math.Max(1, Bounds.Height - 1);

        switch (input.Key.Key)
        {
            case ConsoleKey.UpArrow: Offset -= 1; return true;
            case ConsoleKey.DownArrow: Offset += 1; return true;
            case ConsoleKey.PageUp: Offset -= page; return true;
            case ConsoleKey.PageDown: Offset += page; return true;
            case ConsoleKey.Home: Offset = 0; return true;
            case ConsoleKey.End: Offset = MaxOffset; return true;
            default: return false;
        }
    }

    private bool ScrollWheel(TuiMouseEvent mouse)
    {
        if (mouse.Action != TuiMouseAction.Scroll || !Bounds.Contains(mouse.Column, mouse.Row))
        {
            return false;
        }

        Offset += mouse.Button == TuiMouseButton.ScrollUp ? -1 : 1;
        return true;
    }
}
