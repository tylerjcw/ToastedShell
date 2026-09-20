using Tosh.Runtime;
using Tosh.Tui.Rendering;

namespace Tosh.Tui.Widgets;

/// <summary>
/// One value chosen from a list that opens out of it.
/// </summary>
/// <remarks>
/// <para>
/// A field for a value that has to be one of a few. A list on the page costs a row per
/// option whether or not anyone is choosing; a combo box costs one row and opens when it
/// is asked to (<c>TUI-0023</c>).
/// </para>
/// <para>
/// The list is a <see cref="TuiMenu"/> and the layer is the one a menu bar uses. That is
/// deliberate and is the reason this widget is small: a drop-down anchored to a field and
/// a drop-down anchored to a name on a bar are the same drop-down, and writing the second
/// one again is what this was filed to prevent.
/// </para>
/// </remarks>
public sealed class TuiCombo : TuiWidget, ITuiPopupHost
{
    private readonly TuiMenu _menu = new();
    private readonly TuiBorder _popup;
    private IReadOnlyList<object?> _items = [];
    private int _selected;

    public TuiCombo(IEnumerable<object?>? items = null)
    {
        _popup = new TuiBorder(_menu);
        _menu.Dismiss = () => IsOpen = false;

        if (items is not null)
        {
            Items = [.. items];
        }
    }

    /// <summary>What can be chosen.</summary>
    public IReadOnlyList<object?> Items
    {
        get => _items;
        set
        {
            ArgumentNullException.ThrowIfNull(value);

            _items = value;
            Selected = _selected;
            Rebuild();
        }
    }

    /// <summary>A script function supplying the items, re-read on every redraw.</summary>
    public IShellCallable? ItemsSource { get; set; }

    /// <summary>Which property of an item to show, when an item is an object.</summary>
    public string? DisplayProperty { get; set; }

    /// <summary>Which item is chosen.</summary>
    public int Selected
    {
        get => _selected;
        set => _selected = _items.Count == 0 ? 0 : Math.Clamp(value, 0, _items.Count - 1);
    }

    /// <summary>The chosen item, or null when there is nothing to choose from.</summary>
    public object? SelectedItem => _selected >= 0 && _selected < _items.Count ? _items[_selected] : null;

    /// <summary>Runs when a different item is chosen.</summary>
    public Action<object?>? Changed { get; set; }

    /// <summary>What is shown when there is nothing to choose.</summary>
    public string Placeholder { get; set; } = "—";

    /// <summary>Whether the list is down.</summary>
    public bool IsOpen { get; private set; }

    /// <summary>The mark that says this opens.</summary>
    public string Glyph { get; set; } = "▾";

    public TuiStyle Style { get; set; }

    /// <summary>How it is drawn while it has the keyboard.</summary>
    public TuiStyle SelectedStyle { get; set; } = new(Attributes: TuiTextAttributes.Bold);

    /// <inheritdoc />
    public override bool IsFocusable => true;

    /// <inheritdoc />
    public override object? Value => SelectedItem;

    /// <inheritdoc />
    public TuiWidget? Popup => IsOpen ? _popup : null;

    /// <inheritdoc />
    public TuiWidget? PopupAnchor => this;

    /// <inheritdoc />
    public bool ClosePopup()
    {
        var was = IsOpen;

        IsOpen = false;
        return was;
    }

    /// <summary>Puts the list down, on the item currently chosen.</summary>
    public void Open()
    {
        if (_items.Count == 0)
        {
            return;
        }

        Rebuild();
        _menu.Selected = _selected;
        IsOpen = true;
    }

    /// <summary>What the chosen item reads as.</summary>
    public string Text => SelectedItem is { } item ? Display(item) : Placeholder;

    /// <inheritdoc />
    /// <remarks>The widest option, so the field does not change width as the choice does.</remarks>
    protected override TuiSize MeasureCore(TuiConstraints constraints)
    {
        var widest = _items.Count == 0
            ? TextMeasure.MeasureWidth(Placeholder)
            : _items.Max(item => TextMeasure.MeasureWidth(Display(item)));

        return constraints.Constrain(new TuiSize(widest + 4, 1));
    }

    /// <inheritdoc />
    public override void Draw(TuiSurface surface)
    {
        var style = IsFocused ? SelectedStyle : Style;
        var marker = IsFocused ? "> " : "  ";
        var used = surface.DrawText(0, 0, marker, style);

        // The glyph is kept at the right edge so a column of these lines up, which is what
        // makes a form of them read as a form.
        var room = Math.Max(0, surface.Width - used - TextMeasure.MeasureWidth(Glyph) - 1);

        surface.DrawText(used, 0, TextMeasure.Elide(Text, room), style);
        surface.DrawText(surface.Width - TextMeasure.MeasureWidth(Glyph), 0, Glyph, style);
    }

    /// <inheritdoc />
    public override bool Activate()
    {
        Open();
        return true;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Only what opens it. Once it is open the list has the keyboard, because the list is
    /// the modal on the layer — so moving and choosing are the menu's, not written twice.
    /// </remarks>
    public override bool OnInput(TuiInputEvent input)
    {
        if (!input.IsKey)
        {
            return ClickedOn(input);
        }

        switch (input.Key.Key)
        {
            case ConsoleKey.DownArrow or ConsoleKey.Enter or ConsoleKey.Spacebar or ConsoleKey.F4:
                Open();
                return true;

            // Left and Right step through without opening, which is what a reader who knows
            // the options wants and what every other combo box does.
            case ConsoleKey.LeftArrow: return Step(-1);
            case ConsoleKey.RightArrow: return Step(1);
            default: return false;
        }
    }

    private bool Step(int direction)
    {
        if (_items.Count == 0)
        {
            return false;
        }

        var next = Math.Clamp(_selected + direction, 0, _items.Count - 1);

        if (next == _selected)
        {
            return true;
        }

        Choose(next);
        return true;
    }

    private bool ClickedOn(TuiInputEvent input)
    {
        var mouse = input.Mouse;

        if (mouse.Action != TuiMouseAction.Press ||
            mouse.Button != TuiMouseButton.Left ||
            !Bounds.Contains(mouse.Column, mouse.Row))
        {
            return false;
        }

        if (IsOpen)
        {
            IsOpen = false;
        }
        else
        {
            Open();
        }

        return true;
    }

    private void Choose(int index)
    {
        _selected = index;
        IsOpen = false;
        Changed?.Invoke(SelectedItem);
    }

    /// <summary>Rebuilds the list from the items, each choosing itself.</summary>
    private void Rebuild()
    {
        var built = new List<TuiMenuItem>(_items.Count);

        for (var index = 0; index < _items.Count; index += 1)
        {
            var at = index;

            built.Add(new TuiMenuItem(Display(_items[index]), Chosen: () => Choose(at)));
        }

        _menu.Items = built;
    }

    /// <summary>
    /// What one item reads as.
    /// </summary>
    /// <remarks>
    /// The list's formatter rather than one of its own: it already reads a record's field,
    /// a CLR property and a plain value, and two answers to "what does this item say" is
    /// one more than a reader can be expected to hold.
    /// </remarks>
    private string Display(object? item) => TuiList.FormatValue(item, DisplayProperty);
}
