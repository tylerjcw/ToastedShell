namespace Tosh.Tui.Widgets;

/// <summary>
/// Tracks which widget has the keyboard, and routes input to it.
/// </summary>
/// <remarks>
/// <para>
/// Input used to be handed to a screen, which sorted it out itself, and the order of the
/// tests inside that method was load-bearing with nothing enforcing it. Both browsers
/// check the quit key before they check where focus is, so typing <c>q</c> into either
/// search box quits (<c>TOSH-0011</c>). That is not a slip anyone should be expected to
/// avoid twice — it is what happens when there is no routing.
/// </para>
/// <para>
/// With routing, the question becomes structural. The focused widget sees an event
/// first; only if it declines does the event travel outwards to its ancestors and
/// finally to the screen. A text field consumes printable characters, so a screen-level
/// shortcut never sees the <c>q</c> someone is typing — without the screen having to know
/// a text field exists.
/// </para>
/// </remarks>
public sealed class TuiFocus
{
    private readonly TuiWidget _root;

    /// <summary>Whether the keyboard was taken away on purpose rather than never given.</summary>
    private bool _cleared;

    public TuiFocus(TuiWidget root)
    {
        ArgumentNullException.ThrowIfNull(root);

        _root = root;
        Focused = Focusable().FirstOrDefault();
        Apply();
    }

    /// <summary>The widget currently holding the keyboard, if any.</summary>
    public TuiWidget? Focused { get; private set; }

    /// <summary>Every focusable widget, in the order they appear in the tree.</summary>
    public IReadOnlyList<TuiWidget> Focusable()
    {
        var found = new List<TuiWidget>();
        Collect(_root, found);
        return found;

        static void Collect(TuiWidget widget, List<TuiWidget> into)
        {
            if (widget.IsFocusable)
            {
                into.Add(widget);
            }

            // `FocusChildren`, not `Children`: a container showing a modal answers with the
            // modal alone, so Tab cannot walk into what is behind it.
            foreach (var child in widget.FocusChildren)
            {
                Collect(child, into);
            }
        }
    }

    /// <summary>
    /// Moves the keyboard back into scope if what held it has gone out of reach.
    /// </summary>
    /// <remarks>
    /// A focus scope can change without anything being typed: a handler puts a dialog up,
    /// a tick replaces a pane, a widget is removed. The keyboard was seated when the tree
    /// was built, and if nothing re-seats it, Enter goes to a form the reader can no
    /// longer see while the dialog in front of them does nothing.
    /// </remarks>
    public void Revalidate()
    {
        // Only a widget that has gone out of reach is rescued. Nothing focused is a state
        // a caller can ask for — a screen that wants every key for itself sets it — and
        // seating the keyboard somewhere it was deliberately taken from would override
        // that silently.
        if (Focused is null && _cleared)
        {
            return;
        }

        if (Focused is not null && IndexOf(Focusable(), Focused) >= 0)
        {
            return;
        }

        var reachable = Focusable();

        Focused = reachable.Count > 0 ? reachable[0] : null;
        Apply();
    }

    /// <summary>Gives the keyboard to a particular widget.</summary>
    public void Focus(TuiWidget? widget)
    {
        // Asking for nothing focused is a decision; ending up with nothing focused because
        // what held it disappeared is an accident. Only the second is repaired.
        _cleared = widget is null;
        Focused = widget;
        Apply();
    }

    /// <summary>Moves focus to the next focusable widget, wrapping at the end.</summary>
    public void MoveNext() => Move(1);

    /// <summary>Moves focus to the previous focusable widget, wrapping at the start.</summary>
    public void MovePrevious() => Move(-1);

    private void Move(int direction)
    {
        var order = Focusable();

        if (order.Count == 0)
        {
            Focus(null);
            return;
        }

        var current = Focused is null ? -1 : IndexOf(order, Focused);
        var next = current < 0
            ? (direction > 0 ? 0 : order.Count - 1)
            : ((current + direction) % order.Count + order.Count) % order.Count;

        Focus(order[next]);
    }

    /// <summary>
    /// Gives the keyboard to the focusable widget under a point, if there is one.
    /// </summary>
    /// <remarks>
    /// A click lands on the deepest widget covering the point, which may not itself take
    /// focus — clicking the text inside a list should focus the list. So the search walks
    /// back up from what was hit.
    /// </remarks>
    public bool FocusAt(int column, int row)
    {
        if (_root.HitTest(column, row) is not { } hit)
        {
            return false;
        }

        foreach (var widget in PathTo(hit).Reverse())
        {
            if (widget.IsFocusable)
            {
                Focus(widget);
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Offers an event to the focused widget, then to each ancestor in turn.
    /// </summary>
    /// <returns>Whether anything consumed it.</returns>
    public bool Dispatch(TuiInputEvent input)
    {
        Revalidate();

        var target = Focused;

        if (!input.IsKey && input.Mouse.Action == TuiMouseAction.Press)
        {
            // A click moves focus before it is handled, so the widget clicked is the one
            // that answers for it.
            FocusAt(input.Mouse.Column, input.Mouse.Row);
            target = _root.HitTest(input.Mouse.Column, input.Mouse.Row) ?? Focused;
        }

        if (target is null)
        {
            return _root.OnInput(input);
        }

        foreach (var widget in PathTo(target).Reverse())
        {
            if (widget.OnInput(input))
            {
                return true;
            }
        }

        return false;
    }

    private static int IndexOf(IReadOnlyList<TuiWidget> order, TuiWidget target)
    {
        for (var index = 0; index < order.Count; index += 1)
        {
            if (ReferenceEquals(order[index], target))
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>The widgets from the root down to <paramref name="target"/>, inclusive.</summary>
    /// <summary>
    /// The focused widget and then its ancestors, in the order an event travels.
    /// </summary>
    /// <remarks>
    /// The same order <see cref="Dispatch"/> uses. Exposed so a screen can ask each of them
    /// about its registered keys after all of them have declined the event itself, which
    /// keeps "nearest the keyboard wins" true for shortcuts as well as for input.
    /// </remarks>
    public IReadOnlyList<TuiWidget> FromFocused()
    {
        if (Focused is not null)
        {
            return [.. PathTo(Focused).Reverse()];
        }

        // With nothing focused there is no path to walk, but a screen may still have keys
        // registered somewhere in it — a screen made only of text and shortcuts is an
        // ordinary thing to build. Deepest first, so the innermost table still wins.
        var found = new List<TuiWidget>();

        Collect(_root, found);
        found.Reverse();

        return found;

        static void Collect(TuiWidget widget, List<TuiWidget> into)
        {
            into.Add(widget);

            foreach (var child in widget.FocusChildren)
            {
                Collect(child, into);
            }
        }
    }

    private IReadOnlyList<TuiWidget> PathTo(TuiWidget target)
    {
        var path = new List<TuiWidget>();

        return Walk(_root, target, path) ? path : [target];

        static bool Walk(TuiWidget widget, TuiWidget target, List<TuiWidget> path)
        {
            path.Add(widget);

            if (ReferenceEquals(widget, target))
            {
                return true;
            }

            foreach (var child in widget.Children)
            {
                if (Walk(child, target, path))
                {
                    return true;
                }
            }

            path.RemoveAt(path.Count - 1);
            return false;
        }
    }

    /// <summary>Marks exactly one widget as focused, so it can draw itself that way.</summary>
    private void Apply()
    {
        Mark(_root);

        void Mark(TuiWidget widget)
        {
            widget.IsFocused = ReferenceEquals(widget, Focused);

            foreach (var child in widget.Children)
            {
                Mark(child);
            }
        }
    }
}
