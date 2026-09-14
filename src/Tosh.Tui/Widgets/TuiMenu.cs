using Tosh.Tui.Rendering;

namespace Tosh.Tui.Widgets;

/// <summary>One command on a menu, or the line between two groups of them.</summary>
/// <param name="Key">
/// What a reader would press to do this without the menu. Shown on the right, and not
/// registered — the key belongs to whatever screen the menu is on, and a menu that
/// registered it would answer only while it was open, which is the wrong way round.
/// </param>
public sealed record TuiMenuItem(
    string Label,
    string Key = "",
    Action? Chosen = null,
    bool IsEnabled = true,
    bool IsSeparator = false)
{
    /// <summary>The line between two groups of commands.</summary>
    public static TuiMenuItem Separator { get; } = new(string.Empty, IsSeparator: true);

    /// <summary>Whether the keyboard can land on this item.</summary>
    internal bool IsChoosable => !IsSeparator && IsEnabled;
}

/// <summary>
/// A list of named commands, opened from somewhere and chosen from with the keyboard.
/// </summary>
/// <remarks>
/// <para>
/// A screen can offer a command two ways without this. It can register a key, which is
/// terse and costs no room but is invisible until someone reads the footer, and there are
/// not enough memorable chords for five menus of a dozen. Or it can put a button on the
/// page, which is discoverable and costs a row of the document.
/// </para>
/// <para>
/// A menu is the third answer and the one every editor settled on: the commands are named,
/// grouped, and cost one row whether there are five of them or fifty, because the group
/// opens only when it is asked for (<c>TUI-0023</c>).
/// </para>
/// <para>
/// It draws no border of its own. A popup from a menu bar is wrapped in one; a menu used
/// as a pane is not, and neither should have to undo the other's decision.
/// </para>
/// </remarks>
public sealed class TuiMenu : TuiWidget
{
    private IReadOnlyList<TuiMenuItem> _items = [];
    private int _selected;

    public TuiMenu(string title = "", IEnumerable<TuiMenuItem>? items = null)
    {
        Title = title;

        if (items is not null)
        {
            Items = [.. items];
        }
    }

    /// <summary>The name this menu goes under on a bar.</summary>
    public string Title { get; set; }

    /// <summary>The commands, in order.</summary>
    public IReadOnlyList<TuiMenuItem> Items
    {
        get => _items;
        set
        {
            ArgumentNullException.ThrowIfNull(value);

            _items = value;
            Selected = _selected;
        }
    }

    /// <summary>Which command the keyboard is on.</summary>
    /// <remarks>
    /// Never a separator and never something disabled: those are drawn and cannot be
    /// landed on, so Up and Down step over them rather than stopping on a dead row.
    /// </remarks>
    public int Selected
    {
        get => _selected;
        set => _selected = Nearest(value, 1) is { } found ? found : Nearest(value, -1) ?? 0;
    }

    /// <summary>The command the keyboard is on, if there is one.</summary>
    public TuiMenuItem? SelectedItem
        => _selected >= 0 && _selected < _items.Count && _items[_selected].IsChoosable
            ? _items[_selected]
            : null;

    /// <summary>Runs when a command is chosen, after the command's own handler.</summary>
    public Action<TuiMenuItem>? Chosen { get; set; }

    /// <summary>
    /// Takes the menu down, set by whatever put it up.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Chosen"/> so a bar closing itself does not spend the one
    /// handler an author gets, and so a menu used as a pane simply has nothing here.
    /// </remarks>
    internal Action? Dismiss { get; set; }

    /// <summary>How a disabled command is drawn.</summary>
    public TuiStyle DisabledStyle { get; set; } = new(Attributes: TuiTextAttributes.Dim);

    /// <summary>How the command the keyboard is on is drawn.</summary>
    public TuiStyle SelectedStyle { get; set; } = new(Attributes: TuiTextAttributes.Bold);

    /// <summary>How the key beside a command is drawn.</summary>
    public TuiStyle KeyStyle { get; set; } = new(Attributes: TuiTextAttributes.Dim);

    public TuiStyle Style { get; set; }

    /// <inheritdoc />
    public override bool IsFocusable => true;

    /// <inheritdoc />
    public override object? Value => SelectedItem?.Label;

    /// <summary>Adds a command. Returns this menu.</summary>
    public TuiMenu Add(string label, string key, Action? chosen)
        => Add(new TuiMenuItem(label, key, chosen));

    /// <summary>Adds a command, a separator, or a disabled one. Returns this menu.</summary>
    public TuiMenu Add(TuiMenuItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        Items = [.. _items, item];
        return this;
    }

    /// <summary>Adds the line between two groups of commands. Returns this menu.</summary>
    public TuiMenu AddSeparator() => Add(TuiMenuItem.Separator);

    /// <inheritdoc />
    /// <remarks>
    /// Two columns of padding either side, and a gap of at least two between a label and
    /// its key so the two never read as one string.
    /// </remarks>
    protected override TuiSize MeasureCore(TuiConstraints constraints)
    {
        var labels = _items.Count == 0 ? 0 : _items.Max(item => TuiTextMeasure.MeasureWidth(item.Label));
        var keys = _items.Count == 0 ? 0 : _items.Max(item => TuiTextMeasure.MeasureWidth(item.Key));

        return constraints.Constrain(new TuiSize(
            labels + (keys > 0 ? keys + 2 : 0) + 2,
            _items.Count));
    }

    /// <inheritdoc />
    public override void Draw(TuiSurface surface)
    {
        for (var row = 0; row < _items.Count && row < surface.Height; row += 1)
        {
            var item = _items[row];

            if (item.IsSeparator)
            {
                // Inset by one either side so the line does not run into the frame the
                // popup is drawn in and read as a broken border.
                surface.DrawText(1, row, new string('─', Math.Max(0, surface.Width - 2)), DisabledStyle);
                continue;
            }

            var selected = row == _selected && IsFocused;
            var style = !item.IsEnabled ? DisabledStyle : selected ? SelectedStyle : Style;

            // The whole row takes the style, not just the letters, so the selected command
            // reads as a band rather than as a word that happens to be bold.
            surface.Clip(new TuiRect(0, row, surface.Width, 1)).Fill(style);

            // A marker as well as the style. A band of bold spaces is invisible on a
            // terminal with no colour, which is the same reason a button draws one.
            if (selected)
            {
                surface.DrawText(0, row, "\u25b8", style);
            }

            surface.DrawText(1, row, TuiTextMeasure.Elide(item.Label, Math.Max(0, surface.Width - 2)), style);

            if (item.Key.Length == 0)
            {
                continue;
            }

            var at = surface.Width - 1 - TuiTextMeasure.MeasureWidth(item.Key);

            if (at > TuiTextMeasure.MeasureWidth(item.Label) + 1)
            {
                surface.DrawText(at, row, item.Key, selected ? style : KeyStyle);
            }
        }
    }

    /// <inheritdoc />
    public override bool Activate()
    {
        if (SelectedItem is not { } item)
        {
            return false;
        }

        item.Chosen?.Invoke();
        Chosen?.Invoke(item);
        Dismiss?.Invoke();
        return true;
    }

    /// <inheritdoc />
    public override bool OnInput(TuiInputEvent input)
    {
        if (!input.IsKey)
        {
            return ClickedOn(input);
        }

        switch (input.Key.Key)
        {
            case ConsoleKey.UpArrow: Step(-1); return true;
            case ConsoleKey.DownArrow: Step(1); return true;
            case ConsoleKey.Home: Selected = 0; return true;
            case ConsoleKey.End: Selected = _items.Count - 1; return true;
            case ConsoleKey.Enter or ConsoleKey.Spacebar: return Activate();
            default: return false;
        }
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

        var row = mouse.Row - Bounds.Top;

        if (row < 0 || row >= _items.Count || !_items[row].IsChoosable)
        {
            // A click on a separator or a disabled command is still the menu's: it should
            // not fall through to whatever is underneath.
            return true;
        }

        _selected = row;
        return Activate();
    }

    /// <summary>Moves the keyboard one choosable command in a direction, without wrapping.</summary>
    private void Step(int direction)
    {
        if (Nearest(_selected + direction, direction) is { } found)
        {
            _selected = found;
        }
    }

    /// <summary>The first choosable command from an index, searching in one direction.</summary>
    private int? Nearest(int from, int direction)
    {
        for (var index = Math.Clamp(from, 0, Math.Max(0, _items.Count - 1));
             index >= 0 && index < _items.Count;
             index += direction)
        {
            if (_items[index].IsChoosable)
            {
                return index;
            }
        }

        return null;
    }
}
