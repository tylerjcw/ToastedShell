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
public sealed class TuiDeclarativeScreen : ITuiScreen, ITuiAim, IDisposable
{
    private readonly TuiWidget _root;
    private readonly IReadOnlyList<TuiBinding> _bindings;
    private readonly Func<IShellCallable, object?, object?>? _invoke;
    private readonly TuiFocus _focus;
    private readonly TuiForm? _form;
    private readonly string? _title;
    private readonly Action? _tick;
    private readonly TuiWake? _wake;
    private readonly List<TuiFeeds> _feeds = [];
    private readonly TuiMenuBar? _menus;
    private readonly TuiOverlay? _layers;
    private TuiBorder? _frame;
    private TuiWidget? _returnTo;

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

        // A menu is a layer, so a tree with a bar in it needs somewhere to put one. Wrapped
        // rather than required, because a bar is written where it belongs — one row at the
        // top of a column — and asking an author to also wrap their whole screen in an
        // overlay to make it work would be asking them to say the same thing twice.
        _menus = FindMenus(root);

        if (_menus is not null)
        {
            _layers = root as TuiOverlay ?? new TuiOverlay(root);
            root = _layers;
        }

        _root = _title is null ? root : _frame = new TuiBorder(root, _title)
        {
            TitleStyle = new TuiStyle(Attributes: TuiTextAttributes.Bold),
        };

        // A tree with sources gets a way in from another thread, and one without stays
        // exactly as it was: the loop blocks on the keyboard rather than waiting in slices.
        Collect(_root, _feeds);

        if (_feeds.Count > 0)
        {
            _wake = new TuiWake();

            foreach (var feeds in _feeds)
            {
                feeds.Start(_wake);
            }
        }

        // A footer is a projection of what would answer, so it is told how to ask rather
        // than handed a list: which tables apply depends on where the keyboard is, and
        // that changes when a dialog goes up.
        foreach (var help in Helps(_root))
        {
            help.Tables = () => _focus.FromFocused()
                .Select(widget => widget.Keys)
                .Where(keys => keys is not null)!;
        }

        // Every key table in the tree is pointed at this screen, so a key that names a
        // widget has something to name it to. Done once here rather than at each keystroke
        // because `Keys` is written when the tree is built and does not move.
        foreach (var keys in Tables(_root))
        {
            keys.Aim = this;
        }

        // Visibility is settled before focus is seated. A widget hidden by `When` is
        // visible until its predicate has been asked, so seating the keyboard first put it
        // inside a dialog that was never up — and a dialog with nothing focusable in it
        // left the keyboard nowhere at all.
        if (_invoke is not null)
        {
            TuiBindings.ApplyVisibility(_root, _invoke, ShellRecordUtilities.CreateExpando(Values()));
        }

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
        ShowOpenMenu();

        // Asked before drawing as well as before dispatching, so the caret is drawn where
        // the next keystroke will go even when the scope changed on a tick rather than on
        // a key.
        _focus.Revalidate();

        var buffer = new TuiBuffer(size);
        var bounds = new TuiRect(0, 0, size.Width, size.Height);

        _root.Measure(TuiConstraints.From(size));
        _root.Arrange(bounds);
        _root.Paint(new TuiSurface(buffer, bounds));

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

        // A popup is a layer, so the bar is not one of its ancestors and never sees a key
        // pressed inside it. These three are the ones a reader expects the bar to answer
        // while it is open, so the screen — which owns the layering — answers them.
        if (_menus is { Open: not null })
        {
            switch (input.Key.Key)
            {
                case ConsoleKey.Escape:
                    _menus.Close();
                    return TuiScreenResult.Continue;

                case ConsoleKey.LeftArrow or ConsoleKey.RightArrow:
                    _menus.OnInput(input);
                    return TuiScreenResult.Continue;
            }
        }

        // Registered keys, asked outwards from whatever has the keyboard: a dialog's keys
        // answer before the screen's, and neither is asked while a text field is using the
        // same character as a letter.
        foreach (var widget in _focus.FromFocused())
        {
            if (widget.Keys is { } keys && keys.TryHandle(input.Key, out var shortcut))
            {
                return shortcut == TuiScreenResult.Exit ? TuiScreenResult.Exit : Closed();
            }
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

        // A widget that asked to leave is answered before the form is, because a dialog
        // button saying "yes, discard it" is answering the question the form asked.
        if (AskedToClose(_root))
        {
            Accept();
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

    /// <inheritdoc />
    public TuiWake? Wake => _wake;

    /// <summary>Puts the open menu on the layer above the page, or takes it off.</summary>
    /// <remarks>
    /// Done here rather than by the bar because a popup is not the bar's to draw: a widget
    /// cannot paint outside what it was given, which is the whole reason a layer exists.
    /// Only a menu's own popup is cleared, so a dialog a handler put up is left alone.
    /// </remarks>
    private void ShowOpenMenu()
    {
        if (_layers is null || _menus is null)
        {
            return;
        }

        if (_menus.Open is { } title)
        {
            // Remembered on the way in, so closing hands the keyboard back to whatever the
            // reader was in rather than to the first field on the screen.
            if (_layers.Anchor is not TuiMenuTitle)
            {
                _returnTo = _focus.Focused;
            }

            _layers.Modal = title.Popup;
            _layers.Anchor = title;
        }
        else if (_layers.Anchor is TuiMenuTitle)
        {
            _layers.Modal = null;
            _layers.Anchor = null;

            // Before the revalidation in `Render`, which would otherwise have already
            // seated the keyboard on the first thing it could find.
            Restore();
        }
    }

    /// <summary>The first menu bar in a tree, if it has one.</summary>
    private static TuiMenuBar? FindMenus(TuiWidget widget)
        => widget as TuiMenuBar ?? widget.Children.Select(FindMenus).FirstOrDefault(found => found is not null);

    /// <summary>Stops reading every source, once the loop that was drawing them has gone.</summary>
    public void Dispose()
    {
        foreach (var feeds in _feeds)
        {
            feeds.Stop();
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// A widget that is hidden, not focusable, or behind a modal is not reachable, and a
    /// key aiming at it does nothing. `Focusable` already answers all three, because it
    /// walks focus scopes rather than children.
    /// </remarks>
    public bool Focus(string id)
    {
        if (Find(id) is not { } named || Reachable(named) is not { } target)
        {
            return false;
        }

        // Remembered before the move, so a palette or a search box can hand the keyboard
        // back to whatever the reader was in.
        _returnTo = _focus.Focused;
        _focus.Focus(target);
        return true;
    }

    /// <inheritdoc />
    public bool Press(string id)
        => Find(id) is { IsVisible: true } target && (target.Activate() || Inside(target));

    /// <summary>
    /// The widget an id names, or the one inside it that can actually take the keyboard.
    /// </summary>
    /// <remarks>
    /// A labelled field is a row around an input, and the id is written on the row —
    /// <c>{| Field = "Find", Id = "query" |}</c> — because that is the node the author
    /// wrote. The input is what was meant, which is the same reading bindings already take.
    /// </remarks>
    private TuiWidget? Reachable(TuiWidget named)
    {
        var focusable = _focus.Focusable();

        if (focusable.Contains(named))
        {
            return named;
        }

        return Descendants(named).FirstOrDefault(focusable.Contains);
    }

    /// <summary>Activates the first thing inside a widget that has anything to do.</summary>
    private static bool Inside(TuiWidget widget) => Descendants(widget).Any(child => child.Activate());

    private static IEnumerable<TuiWidget> Descendants(TuiWidget widget)
    {
        foreach (var child in widget.Children)
        {
            yield return child;

            foreach (var nested in Descendants(child))
            {
                yield return nested;
            }
        }
    }

    /// <inheritdoc />
    public bool Restore()
    {
        if (_returnTo is not { } target || !_focus.Focusable().Contains(target))
        {
            return false;
        }

        _returnTo = null;
        _focus.Focus(target);
        return true;
    }

    /// <summary>The widget with an id, anywhere in the tree.</summary>
    private TuiWidget? Find(string id)
    {
        return Walk(_root);

        TuiWidget? Walk(TuiWidget widget)
        {
            if (string.Equals(widget.Id, id, StringComparison.OrdinalIgnoreCase))
            {
                return widget;
            }

            foreach (var child in widget.Children)
            {
                if (Walk(child) is { } found)
                {
                    return found;
                }
            }

            return null;
        }
    }

    /// <summary>Every help widget in a tree.</summary>
    private static IEnumerable<TuiHelp> Helps(TuiWidget widget)
    {
        if (widget is TuiHelp help)
        {
            yield return help;
        }

        foreach (var child in widget.Children)
        {
            foreach (var nested in Helps(child))
            {
                yield return nested;
            }
        }
    }

    /// <summary>Every key table hung anywhere on the tree.</summary>
    private static IEnumerable<TuiShortcuts> Tables(TuiWidget widget)
    {
        if (widget.Keys is { } keys)
        {
            yield return keys;
        }

        foreach (var child in widget.Children)
        {
            foreach (var nested in Tables(child))
            {
                yield return nested;
            }
        }
    }

    /// <summary>Finds every source hung anywhere on the tree.</summary>
    private static void Collect(TuiWidget widget, List<TuiFeeds> into)
    {
        if (widget.Feeds is { } feeds)
        {
            into.Add(feeds);
        }

        foreach (var child in widget.Children)
        {
            Collect(child, into);
        }
    }

    /// <summary>Whether anything in the tree has asked the screen to end.</summary>
    /// <remarks>
    /// Walked rather than subscribed to: a widget has no parent to raise an event on, and
    /// a screen's tree is small enough that asking it is cheaper than keeping a list of
    /// who to ask in step with a tree that can change shape.
    /// </remarks>
    private static bool AskedToClose(TuiWidget widget)
        => widget.ClosesScreen || widget.Children.Any(AskedToClose);

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
