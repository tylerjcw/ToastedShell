using Tosh.Runtime;
using Tosh.Tui.Rendering;

namespace Tosh.Tui.Widgets;

/// <summary>How a form ended, or that it has not.</summary>
public enum TuiFormResult
{
    /// <summary>Still open.</summary>
    Open,

    /// <summary>Accepted, by Enter or by something calling <see cref="TuiForm.Submit"/>.</summary>
    Submitted,

    /// <summary>Abandoned, by Escape or by something calling <see cref="TuiForm.Cancel"/>.</summary>
    Cancelled,
}

/// <summary>
/// A window with a way in and a way out: content, a title, and what to do when the user
/// accepts or abandons it.
/// </summary>
/// <remarks>
/// <para>
/// Without this, a script learned what happened by reading a dictionary the screen handed
/// back — <c>$answers.Values["Width"]</c>, keyed by strings, checked against
/// <c>$answers.Cancelled</c>, with every value arriving as text. The widgets were right
/// there in variables the script had just written, and it went looking for them by name
/// anyway.
/// </para>
/// <para>
/// A form inverts that. <see cref="Submitted"/> runs while the widgets still exist, so it
/// reads them directly, and <see cref="Result"/> says how the form ended so the script
/// does not have to infer it:
/// </para>
/// <code>
/// var width = new TuiField("Width", "2")
/// var form  = new TuiForm(width)
///
/// $form.Title = "New block"
/// $form.Submitted = func() { $blockWidth = Whole($width.Text, 2) }
///
/// tui run $form
///
/// if ($form.WasCancelled) { throw "Cancelled." }
/// </code>
/// <para>
/// Enter accepts and Escape abandons, but only when nothing nearer the keyboard wanted
/// them: both travel outwards from the focused widget, so a multi-line field keeps its
/// newlines and a list with an activation handler keeps its Enter.
/// </para>
/// </remarks>
public sealed class TuiForm : TuiWidget
{
    public TuiForm(TuiWidget? content = null)
    {
        Content = content;
    }

    /// <summary>What the form contains.</summary>
    public TuiWidget? Content { get; set; }

    /// <summary>
    /// The window title, used by <c>tui run</c> when it was not given one.
    /// </summary>
    public string? Title { get; set; }

    /// <summary>Runs when the form is accepted, while its widgets can still be read.</summary>
    /// <remarks>
    /// Handed the form, the way a Windows Forms event is handed its sender. A handler that
    /// holds the widgets in variables ignores it; one built from markup does not have them
    /// and reads <see cref="Values"/> instead.
    /// </remarks>
    public Action<TuiForm>? Submitted { get; set; }

    /// <summary>Runs when the form is abandoned.</summary>
    public Action<TuiForm>? Cancelled { get; set; }

    /// <summary>
    /// What every identified widget in the form is holding, as a record.
    /// </summary>
    /// <remarks>
    /// A record rather than a dictionary because a script reads it as
    /// <c>$form.Values.Width</c>, which is the thing being asked for. C# callers that want
    /// the pairs use <see cref="TuiValues.Collect"/>.
    /// </remarks>
    public object Values => ShellRecordUtilities.CreateExpando(TuiValues.Collect(this));

    /// <summary>How the form ended, or <see cref="TuiFormResult.Open"/> while it has not.</summary>
    public TuiFormResult Result { get; private set; }

    /// <summary>Whether the user accepted the form.</summary>
    public bool WasSubmitted => Result == TuiFormResult.Submitted;

    /// <summary>Whether the user abandoned the form.</summary>
    public bool WasCancelled => Result == TuiFormResult.Cancelled;

    /// <summary>Accepts the form, as a default button would.</summary>
    public void Submit() => Close(TuiFormResult.Submitted, Submitted);

    /// <inheritdoc />
    /// <remarks>Accepting it, which is what pressing Enter on a form does.</remarks>
    public override bool Activate()
    {
        if (Result != TuiFormResult.Open)
        {
            return false;
        }

        Submit();
        return true;
    }

    /// <summary>Abandons the form, as a cancel button would.</summary>
    public void Cancel() => Close(TuiFormResult.Cancelled, Cancelled);

    /// <summary>Abandons the form without asking <see cref="Cancelled"/> about it.</summary>
    /// <remarks>
    /// <para>
    /// The other half of <see cref="KeepOpen"/>. A handler that vetoes leaving puts a
    /// question up; the button that answers it must not ask the same question again, and
    /// <see cref="Cancel"/> would:
    /// </para>
    /// <code>
    /// $form.Cancelled = func() {
    ///     if ($saved) { return }
    ///     $overlay.Modal = $confirm
    ///     $form.KeepOpen()
    /// }
    ///
    /// $discard.Pressed = func() { $form.Discard() }
    /// </code>
    /// </remarks>
    public void Discard() => Close(TuiFormResult.Cancelled, handler: null);

    /// <summary>Changes the form's mind about closing, from inside a handler.</summary>
    /// <remarks>
    /// <para>
    /// The result is set before a handler runs, so a handler can read how the form ended —
    /// and, having read it, decide it has not. This is what lets Escape put a confirmation
    /// up instead of abandoning what the reader typed:
    /// </para>
    /// <code>
    /// $form.Cancelled = func() {
    ///     $overlay.Modal = $confirm
    ///     $form.KeepOpen()
    /// }
    /// </code>
    /// <para>
    /// Outside a handler this does nothing worth doing: the screen has already read the
    /// result and gone.
    /// </para>
    /// </remarks>
    public void KeepOpen() => Result = TuiFormResult.Open;

    /// <summary>The value of one identified widget in the form.</summary>
    /// <remarks>
    /// Not <c>Value</c>: that is what a widget answers a form with, and a method of the
    /// same name hides it.
    /// </remarks>
    public object? ValueOf(string id) => TuiValues.Collect(this).TryGetValue(id, out var value) ? value : null;

    /// <inheritdoc />
    public override IReadOnlyList<TuiWidget> Children => Content is null ? [] : [Content];

    /// <inheritdoc />
    protected override TuiSize MeasureCore(TuiConstraints constraints)
        => Content?.Measure(constraints) ?? constraints.Constrain(new TuiSize(0, 0));

    /// <inheritdoc />
    protected override void ArrangeCore(TuiRect bounds)
    {
        Content?.Arrange(bounds);
    }

    /// <inheritdoc />
    public override void Draw(TuiSurface surface) => DrawChild(Content, surface);

    /// <inheritdoc />
    /// <remarks>
    /// A form is not focusable, so it sees a key only after the focused widget and
    /// everything between have declined it. That is what makes Enter mean "accept this
    /// form" without meaning it inside a text area.
    /// </remarks>
    public override bool OnInput(TuiInputEvent input)
    {
        if (!input.IsKey || Result != TuiFormResult.Open)
        {
            return false;
        }

        switch (input.Key.Key)
        {
            case ConsoleKey.Enter:
                Submit();
                return true;

            case ConsoleKey.Escape:
                Cancel();
                return true;

            default:
                return false;
        }
    }

    /// <remarks>
    /// The result is set before the handler runs, so a handler asking the form how it
    /// ended — or a button handler that submitted it — sees the answer it caused.
    /// </remarks>
    private void Close(TuiFormResult result, Action<TuiForm>? handler)
    {
        if (Result != TuiFormResult.Open)
        {
            return;
        }

        Result = result;
        handler?.Invoke(this);
    }
}
