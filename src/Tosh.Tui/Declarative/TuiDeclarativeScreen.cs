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
    private readonly string? _title;
    private TuiBorder? _frame;

    public TuiDeclarativeScreen(
        TuiWidget root,
        IReadOnlyList<TuiBinding> bindings,
        Func<IShellCallable, object?, object?>? invoke = null,
        string? title = null,
        TimeSpan? refreshInterval = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(bindings);

        _bindings = bindings;
        _invoke = invoke;
        _title = title;
        RefreshInterval = refreshInterval;

        _root = title is null ? root : _frame = new TuiBorder(root, title)
        {
            TitleStyle = new TuiStyle(Attributes: TuiTextAttributes.Bold),
        };

        _focus = new TuiFocus(_root);
    }

    /// <inheritdoc />
    public TimeSpan? RefreshInterval { get; }

    /// <summary>What the user left in the screen, once it has closed.</summary>
    public TuiScreenOutcome? Outcome { get; private set; }

    /// <summary>The current value of every widget that was given an id.</summary>
    public IDictionary<string, object?> Values()
    {
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        Collect(_root);

        return values;

        void Collect(TuiWidget widget)
        {
            if (widget.Id is { Length: > 0 } id)
            {
                values[id] = widget.Value;
            }

            foreach (var child in widget.Children)
            {
                Collect(child);
            }
        }
    }

    public TuiFrame Render(TuiSize size)
    {
        ApplyBindings();

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
    public TuiScreenResult Tick() => TuiScreenResult.Continue;

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
        if (_bindings.Count == 0 || _invoke is null)
        {
            return;
        }

        var values = ShellRecordUtilities.CreateExpando(Values());

        foreach (var binding in _bindings)
        {
            binding.Apply(binding.Widget, _invoke(binding.Source, values));
        }
    }

    public TuiScreenResult HandleInput(TuiInputEvent input)
    {
        // The focused widget first, then its ancestors; the screen acts on what is left.
        if (_focus.Dispatch(input))
        {
            return Outcome is null ? TuiScreenResult.Continue : TuiScreenResult.Exit;
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

            case ConsoleKey.Enter:
                Outcome = new TuiScreenOutcome
                {
                    Selected = [.. Values().Values],
                    Cancelled = false,
                    Values = new Dictionary<string, object?>(Values(), StringComparer.OrdinalIgnoreCase),
                };
                return TuiScreenResult.Exit;

            case ConsoleKey.Escape:
                Outcome = new TuiScreenOutcome { Cancelled = true };
                return TuiScreenResult.Exit;

            default:
                return TuiScreenResult.Continue;
        }
    }
}
