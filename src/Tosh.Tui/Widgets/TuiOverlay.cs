using Tosh.Tui.Rendering;

namespace Tosh.Tui.Widgets;

/// <summary>
/// Shows something on top of something else, and gives it the keyboard while it is up.
/// </summary>
/// <remarks>
/// <para>
/// The screens that needed a dialog spliced its lines into their own output: the
/// confirmation prompt was rendered as text and written over the rows it happened to
/// cover, which is why it could not be clicked, could not be focused, and had to be
/// checked for at the top of every key handler before anything else ran.
/// </para>
/// <para>
/// A dialog is a layer, not a splice. <see cref="Modal"/> draws over
/// <see cref="Content"/> at its own measured size, centred, and while it is up it is the
/// only thing the keyboard and the mouse can reach — the content beneath keeps drawing
/// and stops responding, which is what "modal" means (<c>TUI-0006</c>).
/// </para>
/// <code>
/// var overlay = new TuiOverlay($form)
///
/// $confirm.Pressed = func() { $overlay.Modal = null }
/// $save.Pressed    = func() { $overlay.Modal = $confirmDialog }
/// </code>
/// <para>
/// Setting <see cref="Modal"/> back to <see langword="null"/> dismisses it. Nothing is
/// remembered about what was focused underneath, because focus lands on the first
/// focusable widget of whatever the scope now is, and after a dialog closes that is the
/// content's own first field — which is where a reader expects to be.
/// </para>
/// </remarks>
public sealed class TuiOverlay : TuiWidget
{
    public TuiOverlay(TuiWidget? content = null, TuiWidget? modal = null)
    {
        Content = content;
        Modal = modal;
    }

    /// <summary>What is always drawn.</summary>
    public TuiWidget? Content { get; set; }

    /// <summary>What is drawn on top, and takes the keyboard. Null when nothing is up.</summary>
    public TuiWidget? Modal { get; set; }

    /// <summary>Whether something is currently on top.</summary>
    /// <remarks>
    /// A hidden modal is no modal. That is how a markup screen puts a dialog up: it
    /// declares the dialog once and says when it applies, rather than reaching into a tree
    /// it cannot reach into.
    /// </remarks>
    public bool IsModal => Modal is { IsVisible: true };

    /// <summary>
    /// What the modal opens from, when it belongs to something on the page rather than to
    /// the screen. Null for a dialog, which belongs to the screen and is centred.
    /// </summary>
    /// <remarks>
    /// A dialog is about the screen, so the middle is where it goes. A menu, a completion
    /// list, a tooltip and a colour picker are all about the thing they came out of, and
    /// putting one in the middle of the terminal loses the only piece of information its
    /// position carries. A widget rather than a rectangle, so the anchor follows what it
    /// is anchored to when the layout changes (<c>TUI-0023</c>).
    /// </remarks>
    public TuiWidget? Anchor { get; set; }

    /// <summary>Blank cells between the modal and the edge of the overlay.</summary>
    /// <remarks>
    /// A dialog measured against the full screen can ask for all of it, and one drawn edge
    /// to edge does not read as a layer. The margin is what makes it look like one.
    /// </remarks>
    public int Margin { get; set; } = 2;

    /// <inheritdoc />
    public override IReadOnlyList<TuiWidget> Children
        => (Content, Modal) switch
        {
            (null, null) => [],
            (var content, null) => [content!],
            (null, var modal) => [modal!],
            var (content, modal) => [content!, modal!],
        };

    /// <inheritdoc />
    /// <remarks>
    /// The focus scope. Everything beneath a modal is out of reach until it closes, so the
    /// keyboard cannot land somewhere the reader cannot see it.
    /// </remarks>
    public override IReadOnlyList<TuiWidget> FocusChildren
        => IsModal ? [Modal!] : base.FocusChildren;

    /// <inheritdoc />
    protected override TuiSize MeasureCore(TuiConstraints constraints)
        => Content?.Measure(constraints) ?? constraints.Constrain(new TuiSize(0, 0));

    /// <inheritdoc />
    protected override void ArrangeCore(TuiRect bounds)
    {

        Content?.Arrange(bounds);

        if (IsModal)
        {
            Modal!.Arrange(ModalBounds(bounds));
        }
    }

    /// <inheritdoc />
    public override void Draw(TuiSurface surface)
    {
        DrawChild(Content, surface);

        if (!IsModal)
        {
            return;
        }

        var bounds = ModalBounds(Bounds);

        // Cleared first: the modal is a layer over content that is still drawn, and
        // whatever it does not paint would otherwise show the rows underneath through it.
        var region = new TuiRect(
            bounds.Left - Bounds.Left,
            bounds.Top - Bounds.Top,
            bounds.Width,
            bounds.Height);

        var layer = surface.Clip(region);

        layer.Fill(default);
        Modal.Draw(layer);
    }

    /// <inheritdoc />
    /// <remarks>
    /// A click outside the modal hits the overlay itself rather than the content beneath,
    /// so it is swallowed instead of reaching a widget the reader cannot currently use.
    /// </remarks>
    public override TuiWidget? HitTest(int column, int row)
    {
        if (!IsModal)
        {
            return Content?.HitTest(column, row) ?? base.HitTest(column, row);
        }

        return Modal!.HitTest(column, row) ?? this;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Mouse events that got past hit testing stop here while a modal is up. Keys are left
    /// alone: they reached the overlay only after the modal declined them, and a screen
    /// shortcut is still the screen's to answer.
    /// </remarks>
    public override bool OnInput(TuiInputEvent input) => !input.IsKey && IsModal;

    /// <summary>Where the modal sits: its own measured size, centred, inside the margin.</summary>
    private TuiRect ModalBounds(TuiRect bounds)
    {
        if (!IsModal)
        {
            return default;
        }

        if (Anchor is { IsVisible: true, Bounds.IsEmpty: false } anchor)
        {
            return AnchoredBounds(bounds, anchor.Bounds);
        }

        var room = new TuiConstraints(
            Math.Max(0, bounds.Width - (Margin * 2)),
            Math.Max(0, bounds.Height - (Margin * 2)));

        var wanted = Modal.Measure(room);

        var width = Math.Min(wanted.Width, room.MaxWidth);
        var height = Math.Min(wanted.Height, room.MaxHeight);

        return new TuiRect(
            bounds.Left + ((bounds.Width - width) / 2),
            bounds.Top + ((bounds.Height - height) / 2),
            width,
            height);
    }

    /// <summary>
    /// Where a modal that came out of something sits: under it, or over it when under
    /// would fall off the bottom.
    /// </summary>
    /// <remarks>
    /// No margin. A margin is what stops a dialog looking painted onto the page, and an
    /// anchored popup is meant to look attached to the thing it came out of. Flipping is
    /// what stops a menu near the bottom of the terminal being two rows tall.
    /// </remarks>
    private TuiRect AnchoredBounds(TuiRect bounds, TuiRect anchor)
    {
        var below = Math.Max(0, bounds.Bottom - anchor.Bottom);
        var above = Math.Max(0, anchor.Top - bounds.Top);

        // Measured against the better of the two sides rather than against whichever it
        // will end up on: a menu asked how tall it would like to be should not answer
        // differently because it happens to be near the bottom.
        var wanted = Modal!.Measure(new TuiConstraints(bounds.Width, Math.Max(below, above)));

        var width = Math.Min(wanted.Width, bounds.Width);
        var opensDown = wanted.Height <= below || below >= above;
        var height = Math.Min(wanted.Height, opensDown ? below : above);

        // Left-aligned with the anchor, pulled back onto the screen rather than clipped:
        // a menu opening from the last item on the bar would otherwise lose its right half.
        var left = Math.Min(anchor.Left, Math.Max(bounds.Left, bounds.Right - width));

        return new TuiRect(left, opensDown ? anchor.Bottom : anchor.Top - height, width, height);
    }
}
