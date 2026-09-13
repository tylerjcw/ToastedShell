using Tosh.Runtime;
using Tosh.Tui.Rendering;
using Tosh.Tui.Widgets;

namespace Tosh.Tui.Declarative;

/// <summary>A property that is re-read from a script function rather than set once.</summary>
public sealed record TuiBinding(TuiWidget Widget, IShellCallable Source, Action<TuiWidget, object?> Apply);

/// <summary>
/// Runs a widget tree built from a record, and reports what the user left in it.
/// </summary>
/// <remarks>
/// <para>
/// Replaces the interpreter that read a closed enum of six widget kinds. What it adds
/// beyond hosting a tree is the part that makes a declarative screen worth having: a
/// property written as <c>&amp;Summary</c> is a <em>pull</em> binding, re-read before
/// every redraw, so a screen that shows one thing derived from another needs no handler
/// wiring and no ids to find widgets by (<c>TUI-0004</c>).
/// </para>
/// <para>
/// A binding is called with the form's current values as a record, so it is a function
/// of the screen's state rather than something that has to go looking for it:
/// </para>
/// <code>
/// func Summary(values) { return $"{$values.Width} x {$values.Height}" }
///
/// tui run {| Row = [ {| Field = "Width", Id = "Width" |}, {| Text = &amp;Summary |} ] |}
/// </code>
/// <para>
/// Redraws happen when something arrives — a keystroke, a tick — not on a clock, so
/// "before every redraw" means once per keystroke rather than at a frame rate.
/// </para>
/// </remarks>
public sealed class TuiDeclarativeScreen : ITuiScreen
{
    private readonly TuiWidget _root;
    private readonly IReadOnlyList<TuiBinding> _bindings;
    private readonly Func<IShellCallable, object?, object?>? _invoke;
    private readonly TuiFocus _focus;
    private readonly TuiForm? _form;
    private readonly string? _title;
    private readonly Action? _tick;
    private TuiBorder? _frame;

    public TuiDeclarativeScreen(
        TuiWidget root,
        IReadOnlyList<TuiBinding> bindings,
        Func<IShellCallable, object?, object?>? invoke = null,
        string? title = null,
        TimeSpan? refreshInterval = null,
        Action? tick = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(bindings);

        _tick = tick;
        _bindings = bindings;
        _invoke = invoke;
        _form = FindForm(root);

        // A form names its own window, so `tui run $form` needs no `--title`. An explicit
        // one still wins: the flag is the more specific statement.
        _title = title ?? _form?.Title;
        RefreshInterval = refreshInterval;

        _root = _title is null ? root : _frame = new TuiBorder(root, _title)
        {
            TitleStyle = new TuiStyle(Attributes: TuiTextAttributes.Bold),
        };

        _focus = new TuiFocus(_root);
    }

    /// <summary>The form this screen is showing, if it is showing one.</summary>
    private static TuiForm? FindForm(TuiWidget widget)
    {
        if (widget is TuiForm form)
        {
            return form;
        }

        foreach (var child in widget.Children)
        {
            if (FindForm(child) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <inheritdoc />
    public TimeSpan? RefreshInterval { get; }

    /// <summary>What the user left in the screen, once it has closed.</summary>
    public TuiScreenOutcome? Outcome { get; private set; }

    /// <summary>
    /// Whether the tree contains a <see cref="TuiForm"/>, which changes what closing means.
    /// </summary>
    /// <remarks>
    /// A form has already told the script what happened, through its handlers. Yielding the
    /// widget values a second time would put them on the pipeline behind the script's own
    /// output, so a caller that wants them still asks with <c>--result</c>.
    /// </remarks>
    public bool HasForm => _form is not null;

    /// <summary>The current value of every widget that was given an id.</summary>
    public IDictionary<string, object?> Values() => TuiValues.Collect(_root);

    public TuiFrame Render(TuiSize size)
    {
        ApplyBindings();

        // Asked before drawing as well as before dispatching, so the caret is drawn where
        // the next keystroke will go even when the scope changed on a tick rather than on
        // a key.
        _focus.Revalidate();

        var buffer = new TuiBuffer(size);
        var bounds = new TuiRect(0, 0, size.Width, size.Height);

        _root.Measure(TuiConstraints.From(size));
        _root.Arrange(bounds);
        _root.Draw(new TuiSurface(buffer, bounds));

        if (_focus.Focused is TuiTextField field)
        {
            buffer.Cursor = (field.Bounds.Left + field.CaretColumn, field.Bounds.Top + field.CaretRow);
        }

        return new TuiFrame(buffer);
    }

    /// <inheritdoc />
    public TuiScreenResult Tick()
    {
        // A live screen's own sampling, separate from the pull bindings that run on every
        // redraw: this is what a refresh interval is for.
        _tick?.Invoke();
        return TuiScreenResult.Continue;
    }

    /// <summary>
    /// Re-reads every bound property, passing the form's current values.
    /// </summary>
    /// <remarks>
    /// A binding that throws is left to the runtime's handler guard rather than being
    /// swallowed here: a mistake in a binding should be visible, and the guard already
    /// reports it without ending the screen.
    /// </remarks>
    private void ApplyBindings()
    {
        if (_invoke is null)
        {
            return;
        }

        TuiBindings.Apply(_root, _invoke, ShellRecordUtilities.CreateExpando(Values()));
    }

    public TuiScreenResult HandleInput(TuiInputEvent input)
    {
        // The focused widget first, then its ancestors; the screen acts on what is left.
        if (_focus.Dispatch(input))
        {
            return Closed();
        }

        if (!input.IsKey)
        {
            return TuiScreenResult.Continue;
        }

        switch (input.Key.Key)
        {
            case ConsoleKey.Tab:
                if (input.Key.Modifiers.HasFlag(ConsoleModifiers.Shift))
                {
                    _focus.MovePrevious();
                }
                else
                {
                    _focus.MoveNext();
                }

                return TuiScreenResult.Continue;

            // A form owns these keys, and has already been offered them on the way out
            // from the focused widget. Acting on them again here would submit a form its
            // own handler had just declined to close.
            case ConsoleKey.Enter when _form is null:
                Accept();
                return TuiScreenResult.Exit;

            case ConsoleKey.Escape when _form is null:
                Outcome = new TuiScreenOutcome { Cancelled = true };
                return TuiScreenResult.Exit;

            default:
                return TuiScreenResult.Continue;
        }
    }

    /// <summary>
    /// Ends the screen once the form it is showing has ended.
    /// </summary>
    /// <remarks>
    /// Asked after every dispatched event rather than only after a key, because a form is
    /// closed by whatever calls <c>Submit</c> — a default button, a list's activation
    /// handler, a script function reacting to something else entirely.
    /// </remarks>
    private TuiScreenResult Closed()
    {
        if (Outcome is not null)
        {
            return TuiScreenResult.Exit;
        }

        switch (_form?.Result)
        {
            case TuiFormResult.Submitted:
                Accept();
                return TuiScreenResult.Exit;

            case TuiFormResult.Cancelled:
                Outcome = new TuiScreenOutcome { Cancelled = true };
                return TuiScreenResult.Exit;

            default:
                return TuiScreenResult.Continue;
        }
    }

    /// <summary>Records what every identified widget holds, for a caller that asked for it.</summary>
    private void Accept()
    {
        var values = Values();

        Outcome = new TuiScreenOutcome
        {
            Selected = [.. values.Values],
            Cancelled = false,
            Values = new Dictionary<string, object?>(values, StringComparer.OrdinalIgnoreCase),
        };
    }
}
