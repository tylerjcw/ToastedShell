using Tosh.Tui.Rendering;

namespace Tosh.Tui.Widgets;

/// <summary>A confirmation dialog: a message, two answers, and which one is selected.</summary>
/// <remarks>
/// <para>
/// `TUI-0002`. <see cref="TuiConfirmationDialogState"/> tracks the answer and reads the keys
/// and does not draw, so drawing belonged to whichever screen owned one — which is how the
/// config browser came to carry its dialog's layout by hand. This is the drawing half, and
/// it <em>wraps</em> that state rather than restating it: the key handling and the wording
/// of the entries are the tested ones, unchanged.
/// </para>
/// <para>
/// Being a widget is what makes it reachable from a script. A screen written in markup can
/// ask a question without the script knowing how a dialog is laid out, which is the whole
/// point of the widget contract.
/// </para>
/// </remarks>
public sealed class TuiConfirm : TuiWidget
{
    private readonly TuiConfirmationDialogState _state = new();
    private readonly TuiLines _lines = new();
    private readonly TuiBorder _frame;

    public TuiConfirm(string? message = null, string? title = null)
    {
        _frame = new TuiBorder(_lines);
        Message = message ?? string.Empty;
        Title = title ?? string.Empty;
    }

    /// <summary>The dialog's heading.</summary>
    public string Title
    {
        get => _state.Title;
        set => Reopen(title: value);
    }

    /// <summary>What is being asked. Wrapped to the available width when drawn.</summary>
    public string Message
    {
        get => _state.Message;
        set => Reopen(message: value);
    }

    /// <summary>The affirmative answer's wording.</summary>
    public string ConfirmLabel
    {
        get => _state.ConfirmLabel;
        set => Reopen(confirmLabel: value);
    }

    /// <summary>The negative answer's wording.</summary>
    public string CancelLabel
    {
        get => _state.CancelLabel;
        set => Reopen(cancelLabel: value);
    }

    /// <summary>Whether the affirmative answer is the selected one.</summary>
    public bool ConfirmSelected
    {
        get => _state.ConfirmSelected;
        set => _state.ConfirmSelected = value;
    }

    /// <summary>
    /// Raised with the answer: <see langword="true"/> for confirmed, <see langword="false"/>
    /// for cancelled.
    /// </summary>
    /// <remarks>
    /// One handler rather than two, because a dialog that is dismissed has been answered —
    /// a caller that ignores the cancellation has a bug, and two handlers make it easy to
    /// write by only supplying one.
    /// </remarks>
    public Action<bool>? Answered { get; set; }

    /// <summary>The frame's style, for a dialog that wants to read as a warning.</summary>
    public TuiStyle Style
    {
        get => _frame.Style;
        set => _frame.Style = value;
    }

    /// <summary>The style the message and the answers are drawn in.</summary>
    public TuiStyle TextStyle { get; set; }

    /// <summary>
    /// The widest the dialog will be drawn, in columns.
    /// </summary>
    /// <remarks>
    /// Sized to the message rather than to the screen, as the config browser's dialog was:
    /// a confirmation that fills the window is a screen, and the reader loses the thing they
    /// are deciding about.
    /// </remarks>
    public int MaxWidth { get; set; } = 72;

    /// <summary>The narrowest it will be drawn, so a one-word question is still a dialog.</summary>
    public int MinWidth { get; set; } = 24;

    /// <summary>
    /// Whether answering closes the screen the dialog is on. On by default.
    /// </summary>
    /// <remarks>
    /// A button's <c>Exit</c> is opt-in because most buttons do not end anything. A dialog
    /// is the other way round: it exists to be answered and taken down, and one that stays
    /// up after an answer leaves the reader with no way out — Escape answers it too, so it
    /// would not even be an escape. Set it false to keep one embedded in a larger form.
    /// </remarks>
    public bool ClosesOnAnswer { get; set; } = true;

    public override IReadOnlyList<TuiWidget> Children => [_frame];

    public override bool IsFocusable => true;

    protected override TuiSize MeasureCore(TuiConstraints constraints)
    {
        Compose(InnerWidthFor(constraints.MaxWidth));
        return _frame.Measure(constraints);
    }

    protected override void ArrangeCore(TuiRect bounds)
    {
        Compose(InnerWidthFor(bounds.Width));
        _frame.Arrange(bounds);
    }

    public override void Draw(TuiSurface surface) => _frame.Draw(surface);

    /// <inheritdoc />
    /// <remarks>
    /// The keys are the state's, so <c>y</c>, <c>n</c>, the arrows, Tab, Enter and Escape
    /// mean here exactly what they mean in the config browser.
    /// </remarks>
    public override bool OnInput(TuiInputEvent input)
    {
        if (!input.IsKey)
        {
            return false;
        }

        var result = _state.HandleKey(input.Key);

        switch (result.Kind)
        {
            case TuiConfirmationDialogResultKind.Confirmed:
                Answer(true);
                return true;
            case TuiConfirmationDialogResultKind.Cancelled:
                Answer(false);
                return true;
            default:
                // A selection change is still handled: the arrows belong to the dialog while
                // it is up, and letting them past would move whatever is behind it.
                return input.Key.Key is ConsoleKey.LeftArrow or ConsoleKey.RightArrow or ConsoleKey.Tab;
        }
    }

    private void Answer(bool confirmed)
    {
        Answered?.Invoke(confirmed);

        if (ClosesOnAnswer)
        {
            ClosesScreen = true;
        }
    }

    /// <summary>The room inside the frame, clamped so the dialog stays a dialog.</summary>
    private int InnerWidthFor(int available)
    {
        var usable = available <= 0 ? MaxWidth : available - 2;
        return Math.Clamp(usable, Math.Min(MinWidth, Math.Max(usable, 1)), Math.Max(MaxWidth, MinWidth));
    }

    private void Compose(int inner)
    {
        _frame.Title = _state.Title;
        _lines.Lines =
        [
            .. _state.BuildEntries(inner)
                .Select(text => new TuiSpanLine([new TuiSpan(text, TextStyle)])),
        ];
    }

    /// <summary>
    /// Re-opens the underlying state with one field changed.
    /// </summary>
    /// <remarks>
    /// The state exposes its wording through <c>Open</c> and keeps the setters private, so a
    /// property here has to go back through it. Cheap, and it keeps one copy of the defaults.
    /// </remarks>
    private void Reopen(
        string? title = null,
        string? message = null,
        string? confirmLabel = null,
        string? cancelLabel = null)
        => _state.Open(
            title ?? _state.Title,
            message ?? _state.Message,
            confirmLabel ?? _state.ConfirmLabel,
            cancelLabel ?? _state.CancelLabel,
            _state.ConfirmSelected);
}
