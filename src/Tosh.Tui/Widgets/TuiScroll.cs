using Tosh.Tui.Rendering;

namespace Tosh.Tui.Widgets;

/// <summary>Shows a window onto a child taller than the space available.</summary>
/// <remarks>
/// <para>
/// Scrolling is not a property of a list or a document — it is a relationship between a
/// viewport and content larger than it, which is what a container expresses. The TUI had
/// a <see cref="TuiScrollState"/> and then every widget that scrolled kept one and drove
/// it by hand, recomputing a page size from a height at each call site (<c>TUI-0007</c>).
/// </para>
/// <para>
/// The child is drawn into a buffer at its full height and the visible rows are
/// composited out of it. That costs a buffer per frame for scrollable content, which is
/// what compositing is for — the alternative is letting children draw outside their
/// bounds, and a surface that cannot be trusted to clip is not worth having.
/// </para>
/// </remarks>
public sealed class TuiScroll : TuiWidget
{
    private int _contentHeight;

    public TuiScroll(TuiWidget? child = null)
    {
        Child = child;
    }

    public TuiWidget? Child { get; set; }

    /// <summary>The first content row shown.</summary>
    public int Offset { get; private set; }

    /// <summary>The child's full height, as last measured.</summary>
    public int ContentHeight => _contentHeight;

    /// <summary>How far the offset can go before the last row is at the bottom.</summary>
    public int MaxOffset => Math.Max(0, _contentHeight - Bounds.Height);

    /// <summary>
    /// Focusable only when its child is not.
    /// </summary>
    /// <remarks>
    /// A scroll container around a document takes the keyboard, because scrolling is the
    /// only thing to do with a document. Around a list it must not: both would be
    /// focusable, the wrapper comes first in tree order, and an arrow key would scroll
    /// the view instead of moving the selection. The child takes focus and asks to be
    /// kept visible through <see cref="ScrollIntoView"/>.
    /// </remarks>
    public override bool IsFocusable => Child is null || !Child.IsFocusable;

    /// <inheritdoc />
    /// <remarks>
    /// A scroll container holds nothing of its own; it shows what its child holds. A list
    /// wrapped in one for scrolling should still answer for its selection, or naming the
    /// wrapper would lose the value the author was asking for.
    /// </remarks>
    public override object? Value => Child?.Value;

    public override IReadOnlyList<TuiWidget> Children => Child is null ? [] : [Child];

    public override TuiSize Measure(TuiConstraints constraints)
    {
        // The child is measured with no height limit: how tall it wants to be is exactly
        // what decides whether there is anything to scroll.
        var desired = Child?.Measure(new TuiConstraints(constraints.MaxWidth, int.MaxValue))
                      ?? new TuiSize(0, 0);

        _contentHeight = desired.Height;

        return constraints.Constrain(desired);
    }

    public override void Arrange(TuiRect bounds)
    {
        base.Arrange(bounds);

        if (Child is null)
        {
            return;
        }

        // Arranged at its own full height, in its own coordinates. Where it appears is
        // decided at draw time by which rows are copied out.
        Child.Arrange(new TuiRect(0, 0, bounds.Width, Math.Max(_contentHeight, bounds.Height)));
        Clamp();
    }

    public override void Draw(TuiSurface surface)
    {
        if (Child is null || surface.Width <= 0 || surface.Height <= 0)
        {
            return;
        }

        var height = Math.Max(_contentHeight, surface.Height);
        var content = new TuiBuffer(new TuiSize(surface.Width, height));

        Child.Draw(new TuiSurface(content));

        for (var row = 0; row < surface.Height; row += 1)
        {
            for (var column = 0; column < surface.Width; column += 1)
            {
                surface.Set(column, row, content[column, row + Offset]);
            }
        }
    }

    /// <summary>Scrolls by a number of rows, stopping at either end.</summary>
    public void ScrollBy(int rows)
    {
        Offset += rows;
        Clamp();
    }

    /// <summary>Scrolls to put a content row inside the viewport.</summary>
    /// <remarks>
    /// This is how a child asks to be seen — a list keeping its selected row visible —
    /// without reaching into the container's offset itself.
    /// </remarks>
    public void ScrollIntoView(int contentRow)
    {
        if (Bounds.Height <= 0)
        {
            return;
        }

        if (contentRow < Offset)
        {
            Offset = contentRow;
        }
        else if (contentRow >= Offset + Bounds.Height)
        {
            Offset = contentRow - Bounds.Height + 1;
        }

        Clamp();
    }

    public void ScrollToTop() => ScrollBy(int.MinValue / 2);

    public void ScrollToBottom() => ScrollBy(int.MaxValue / 2);

    private void Clamp() => Offset = Math.Clamp(Offset, 0, MaxOffset);

    /// <summary>
    /// Answers in content coordinates, because that is where the child was placed.
    /// </summary>
    /// <remarks>
    /// A scrolled child is arranged at its own full height starting from zero, not at the
    /// position it appears on screen — so a point has to be converted before the child can
    /// say whether it covers it. Without this, clicking the fifth visible row of a list
    /// scrolled halfway down selects the fifth row of the list.
    /// </remarks>
    public override TuiWidget? HitTest(int column, int row)
    {
        if (!Bounds.Contains(column, row))
        {
            return null;
        }

        return Child?.HitTest(column - Bounds.Left, row - Bounds.Top + Offset) ?? this;
    }

    /// <summary>The same event, addressed to the content rather than to the screen.</summary>
    private TuiInputEvent ToContent(TuiInputEvent input)
    {
        var mouse = input.Mouse;

        return TuiInputEvent.FromMouse(new TuiMouseEvent(
            mouse.Action,
            mouse.Button,
            mouse.Column - Bounds.Left,
            mouse.Row - Bounds.Top + Offset,
            mouse.Shift,
            mouse.Alt,
            mouse.Control));
    }

    public override bool OnInput(TuiInputEvent input)
    {
        if (!input.IsKey)
        {
            // The child sees the click in its own coordinates, and only if it declines
            // does this become a scroll.
            if (Child is not null &&
                input.Mouse.Action == TuiMouseAction.Press &&
                Bounds.Contains(input.Mouse.Column, input.Mouse.Row) &&
                Child.OnInput(ToContent(input)))
            {
                return true;
            }

            return ScrollWheel(input);
        }

        if (!IsFocused)
        {
            return false;
        }

        var page = Math.Max(1, Bounds.Height - 1);

        switch (input.Key.Key)
        {
            case ConsoleKey.UpArrow: ScrollBy(-1); return true;
            case ConsoleKey.DownArrow: ScrollBy(1); return true;
            case ConsoleKey.PageUp: ScrollBy(-page); return true;
            case ConsoleKey.PageDown: ScrollBy(page); return true;
            case ConsoleKey.Home: ScrollToTop(); return true;
            case ConsoleKey.End: ScrollToBottom(); return true;
            default: return false;
        }
    }

    /// <summary>
    /// The wheel scrolls whatever it is over, focused or not — which is what a wheel does.
    /// </summary>
    private bool ScrollWheel(TuiInputEvent input)
    {
        var mouse = input.Mouse;

        if (mouse.Action != TuiMouseAction.Scroll || !Bounds.Contains(mouse.Column, mouse.Row))
        {
            return false;
        }

        ScrollBy(mouse.Button == TuiMouseButton.ScrollUp ? -3 : 3);
        return true;
    }
}
