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
    public bool IsModal => Modal is not null;

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
        => Modal is null ? Children : [Modal];

    /// <inheritdoc />
    protected override TuiSize MeasureCore(TuiConstraints constraints)
        => Content?.Measure(constraints) ?? constraints.Constrain(new TuiSize(0, 0));

    /// <inheritdoc />
    protected override void ArrangeCore(TuiRect bounds)
    {

        Content?.Arrange(bounds);
        Modal?.Arrange(ModalBounds(bounds));
    }

    /// <inheritdoc />
    public override void Draw(TuiSurface surface)
    {
        DrawChild(Content, surface);

        if (Modal is null)
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
        if (Modal is null)
        {
            return Content?.HitTest(column, row) ?? base.HitTest(column, row);
        }

        return Modal.HitTest(column, row) ?? this;
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
        if (Modal is null)
        {
            return default;
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
}
