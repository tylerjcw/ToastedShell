using System.Reflection;
using Tosh.Runtime;
using Tosh.Tui.Rendering;

namespace Tosh.Tui.Widgets;

/// <summary>A selectable list of values.</summary>
/// <remarks>
/// <para>
/// Draws at its full height and expects to be inside a <see cref="TuiScroll"/> — see
/// <see cref="Scrollable"/>, which wires the two together so moving the selection keeps
/// it visible. Scrolling is the container's job (<c>TUI-0007</c>); choosing is this
/// widget's.
/// </para>
/// <para>
/// Items are whatever the caller has. A script's list is normally records or
/// dictionaries, so the display lookup asks the shell before it asks reflection.
/// </para>
/// </remarks>
public sealed class TuiList : TuiWidget
{
    private readonly HashSet<int> _checked = [];
    private int _selectedIndex;

    public TuiList(IReadOnlyList<object?>? items = null)
    {
        Items = items ?? [];
    }

    /// <summary>The values shown, in order.</summary>
    /// <remarks>
    /// Replacing them keeps the selected index where it was, clamped into range, so a
    /// list being refreshed underneath someone does not jump back to the top while they
    /// are reading it.
    /// </remarks>
    public IReadOnlyList<object?> Items
    {
        get;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            field = value;
            SelectedIndex = _selectedIndex;
        }
    } = [];

    /// <summary>A property name to show instead of the value itself.</summary>
    public string? DisplayProperty { get; set; }

    /// <summary>A function to render an item, taking precedence over the property.</summary>
    public Func<object?, string>? DisplaySelector { get; set; }

    /// <summary>Allows more than one item to be ticked.</summary>
    public bool MultiSelect { get; set; }

    /// <summary>The scroll container to keep the selection visible in.</summary>
    public TuiScroll? Viewport { get; set; }

    public TuiStyle Style { get; set; }

    public TuiStyle SelectedStyle { get; set; } = new(Attributes: TuiTextAttributes.Bold);

    /// <summary>Raised when the selection moves.</summary>
    public Action<int>? SelectionChanged { get; set; }

    /// <summary>Raised when an item is chosen with Enter.</summary>
    public Action<object?>? Activated { get; set; }

    /// <summary>Asks whether an item is ticked, instead of the list remembering.</summary>
    /// <remarks>
    /// A list that filters cannot hold ticks by index: the indices mean something
    /// different after every keystroke in a search box, so ticks would move to whatever
    /// happened to take their place. Set this together with <see cref="CheckedToggled"/>
    /// and the owner keeps the truth — usually keyed to the unfiltered collection.
    /// </remarks>
    public Func<object?, bool>? IsChecked { get; set; }

    /// <summary>Told that an item was ticked or unticked, when the owner holds the ticks.</summary>
    public Action<object?>? CheckedToggled { get; set; }

    /// <inheritdoc />
    /// <remarks>Ticked items when multi-select is on, otherwise the highlighted one.</remarks>
    public override object? Value => MultiSelect ? CheckedItems : SelectedItem;

    public override bool IsFocusable => true;

    /// <summary>Which item the keyboard is on.</summary>
    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            var clamped = Items.Count == 0 ? 0 : Math.Clamp(value, 0, Items.Count - 1);

            if (clamped == _selectedIndex)
            {
                return;
            }

            _selectedIndex = clamped;
            Viewport?.ScrollIntoView(clamped);
            SelectionChanged?.Invoke(clamped);
        }
    }

    /// <summary>The selected value, or null when the list is empty.</summary>
    public object? SelectedItem
        => Items.Count == 0 ? null : Items[Math.Clamp(_selectedIndex, 0, Items.Count - 1)];

    /// <summary>The ticked items, when <see cref="MultiSelect"/> is on.</summary>
    /// <remarks>
    /// Reports what this list is holding. When an owner holds the ticks through
    /// <see cref="IsChecked"/>, ask the owner instead — this sees only what is currently
    /// in <see cref="Items"/>.
    /// </remarks>
    public IReadOnlyList<object?> CheckedItems
        => IsChecked is null
            ? _checked.Where(index => index < Items.Count).OrderBy(index => index).Select(index => Items[index]).ToArray()
            : Items.Where(item => IsChecked(item)).ToArray();

    /// <summary>Wraps a list in a scroll container, wired so the selection stays visible.</summary>
    public static TuiScroll Scrollable(TuiList list)
    {
        ArgumentNullException.ThrowIfNull(list);

        var scroll = new TuiScroll(list);
        list.Viewport = scroll;

        return scroll;
    }

    /// <summary>How an item is shown.</summary>
    public string Format(object? item)
    {
        if (DisplaySelector is not null)
        {
            return DisplaySelector(item) ?? string.Empty;
        }

        return FormatValue(item, DisplayProperty);
    }

    /// <summary>
    /// Renders a value under a display property, whatever shape the value is.
    /// </summary>
    /// <remarks>
    /// A script's list is normally records or dictionaries, and their fields are not CLR
    /// properties — reflection alone finds nothing on them, so every row falls back to
    /// <c>ToString()</c> and the list reads as a column of type names. Shell records are
    /// asked first, then reflection for ordinary CLR objects.
    /// </remarks>
    public static string FormatValue(object? item, string? displayProperty)
    {
        if (item is null)
        {
            return "(null)";
        }

        if (displayProperty is not null)
        {
            if (ShellRecordUtilities.TryGetValue(item, displayProperty, out var recordValue))
            {
                return recordValue?.ToString() ?? string.Empty;
            }

            var property = item.GetType().GetProperty(
                displayProperty,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);

            if (property is not null)
            {
                return property.GetValue(item)?.ToString() ?? string.Empty;
            }
        }

        return item.ToString() ?? string.Empty;
    }

    public override TuiSize Measure(TuiConstraints constraints)
    {
        var width = 0;

        foreach (var item in Items)
        {
            width = Math.Max(width, TuiTextMeasure.MeasureWidth(Format(item)) + MarkerWidth);
        }

        return constraints.Constrain(new TuiSize(width, Items.Count));
    }

    private int MarkerWidth => MultiSelect ? 6 : 2;

    public override void Draw(TuiSurface surface)
    {
        for (var index = 0; index < Items.Count && index < surface.Height; index += 1)
        {
            var isSelected = index == _selectedIndex;
            var style = isSelected ? SelectedStyle : Style;

            var marker = isSelected ? "> " : "  ";

            if (MultiSelect)
            {
                var ticked = IsChecked is null ? _checked.Contains(index) : IsChecked(Items[index]);
                marker += ticked ? "[x] " : "[ ] ";
            }

            var used = surface.DrawText(0, index, marker, style);
            surface.DrawText(used, index, Format(Items[index]), style);
        }
    }

    /// <summary>Ticks or unticks the selected item.</summary>
    public void ToggleChecked()
    {
        if (!MultiSelect || Items.Count == 0)
        {
            return;
        }

        if (CheckedToggled is not null)
        {
            CheckedToggled(SelectedItem);
            return;
        }

        if (!_checked.Add(_selectedIndex))
        {
            _checked.Remove(_selectedIndex);
        }
    }

    public override bool OnInput(TuiInputEvent input)
    {
        if (!input.IsKey)
        {
            return ClickToSelect(input);
        }

        if (!IsFocused)
        {
            return false;
        }

        var page = Math.Max(1, Bounds.Height - 1);

        switch (input.Key.Key)
        {
            case ConsoleKey.UpArrow: SelectedIndex -= 1; return true;
            case ConsoleKey.DownArrow: SelectedIndex += 1; return true;
            case ConsoleKey.PageUp: SelectedIndex -= page; return true;
            case ConsoleKey.PageDown: SelectedIndex += page; return true;
            case ConsoleKey.Home: SelectedIndex = 0; return true;
            case ConsoleKey.End: SelectedIndex = Items.Count - 1; return true;

            case ConsoleKey.Spacebar when MultiSelect:
                ToggleChecked();
                return true;

            case ConsoleKey.Enter:
                Activated?.Invoke(SelectedItem);
                return true;

            default:
                return false;
        }
    }

    private bool ClickToSelect(TuiInputEvent input)
    {
        var mouse = input.Mouse;

        if (mouse.Action != TuiMouseAction.Press || mouse.Button != TuiMouseButton.Left)
        {
            return false;
        }

        // Inside a scroll container this widget is arranged in content coordinates — at
        // its own full height, starting from zero — while the event carries the position
        // on screen. The viewport is what knows the difference between the two.
        if (Viewport is { } viewport)
        {
            if (!viewport.Bounds.Contains(mouse.Column, mouse.Row))
            {
                return false;
            }

            SelectedIndex = mouse.Row - viewport.Bounds.Top + viewport.Offset;
            return true;
        }

        if (!Bounds.Contains(mouse.Column, mouse.Row))
        {
            return false;
        }

        SelectedIndex = mouse.Row - Bounds.Top;
        return true;
    }
}
