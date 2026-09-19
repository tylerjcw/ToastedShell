using Tosh.Tui.Rendering;

namespace Tosh.Tui.Widgets;

/// <summary>One name on a menu bar, and the menu it opens.</summary>
/// <remarks>
/// A widget of its own so the popup has something to be anchored to. A menu cannot be its
/// own anchor: it is arranged twice, once as a name on the bar and once as the popup, and
/// the second would overwrite the rectangle the first had to supply.
/// </remarks>
public sealed class TuiMenuTitle : TuiWidget
{
    internal TuiMenuTitle(TuiMenu menu)
    {
        Menu = menu;
        Popup = new TuiBorder(menu);
    }

    /// <summary>The menu this name opens.</summary>
    public TuiMenu Menu { get; }

    /// <summary>The menu in its frame, ready to be laid over the page.</summary>
    /// <remarks>
    /// Built once rather than per frame. A popup rebuilt each time it is shown is a
    /// different widget each time, so the keyboard would have to be re-seated in it and
    /// nothing about it could be remembered.
    /// </remarks>
    internal TuiWidget Popup { get; }

    /// <summary>What the bar shows.</summary>
    public string Label => Menu.Title;

    /// <summary>Whether this menu is the one currently down.</summary>
    public bool IsOpen { get; internal set; }

    /// <inheritdoc />
    protected override TuiSize MeasureCore(TuiConstraints constraints)
        => constraints.Constrain(new TuiSize(TuiTextMeasure.MeasureWidth(Label) + 2, 1));

    /// <inheritdoc />
    public override void Draw(TuiSurface surface)
        => surface.DrawText(1, 0, TuiTextMeasure.Elide(Label, Math.Max(0, surface.Width - 2)), default);
}

/// <summary>
/// A row of named menus, one of which can be down at a time.
/// </summary>
/// <remarks>
/// <para>
/// The bar itself is the only part that costs room: one line, whether there are two menus
/// or nine. The popup is a layer, so it is not the bar's to draw — the bar says which menu
/// is open and where its name sits, and whatever owns the tree puts it on screen
/// (<c>TUI-0023</c>).
/// </para>
/// <para>
/// Left and Right walk the bar. Down or Enter opens; Escape closes and hands the keyboard
/// back. A letter opens the menu whose name starts with it, which is what a reader who has
/// used any editor will try.
/// </para>
/// </remarks>
public sealed class TuiMenuBar : TuiWidget, ITuiPopupHost
{
    private readonly List<TuiMenuTitle> _titles = [];
    private int _highlighted;

    public TuiMenuBar(IEnumerable<TuiMenu>? menus = null)
    {
        if (menus is not null)
        {
            Menus = [.. menus];
        }
    }

    /// <summary>The menus, in the order they sit on the bar.</summary>
    public IReadOnlyList<TuiMenu> Menus
    {
        get => [.. _titles.Select(title => title.Menu)];
        set
        {
            ArgumentNullException.ThrowIfNull(value);

            _titles.Clear();
            _titles.AddRange(value.Select(menu => new TuiMenuTitle(menu) { Size = TuiLength.Auto }));
            _highlighted = 0;
        }
    }

    /// <summary>The names on the bar.</summary>
    public override IReadOnlyList<TuiWidget> Children => _titles;

    /// <summary>The menu currently down, or null.</summary>
    public TuiMenuTitle? Open => _titles.FirstOrDefault(title => title.IsOpen);

    /// <inheritdoc />
    public TuiWidget? Popup => Open?.Popup;

    /// <inheritdoc />
    /// <remarks>The name it came out of, not the bar: a menu opens under its own title.</remarks>
    public TuiWidget? PopupAnchor => Open;

    /// <inheritdoc />
    public bool ClosePopup() => Close();

    /// <summary>Which name the keyboard is on.</summary>
    public int Highlighted
    {
        get => _highlighted;
        set => _highlighted = _titles.Count == 0 ? 0 : Math.Clamp(value, 0, _titles.Count - 1);
    }

    /// <summary>Blank cells between one name and the next.</summary>
    public int Gap { get; set; }

    /// <summary>How the name of the open menu is drawn.</summary>
    public TuiStyle OpenStyle { get; set; } = new(Attributes: TuiTextAttributes.Bold);

    /// <summary>How the name the keyboard is on is drawn while nothing is open.</summary>
    public TuiStyle HighlightedStyle { get; set; } = new(Attributes: TuiTextAttributes.Underline);

    public TuiStyle Style { get; set; }

    /// <inheritdoc />
    /// <remarks>
    /// Only while something is down. A bar that took the keyboard like any other widget
    /// would take it <em>first</em>, being at the top of the tree, so the document below it
    /// would not have it until the reader pressed Tab — and a letter typed at a screen that
    /// had just opened would open a menu instead of being typed. A bar is reached the way
    /// every editor reaches one: a key aimed at it (<c>Press = "menus"</c>, or a click).
    /// </remarks>
    public override bool IsFocusable => Open is not null;

    /// <inheritdoc />
    public override object? Value => Open?.Label;

    /// <summary>Adds a menu. Returns this bar.</summary>
    public TuiMenuBar Add(TuiMenu menu)
    {
        ArgumentNullException.ThrowIfNull(menu);

        _titles.Add(new TuiMenuTitle(menu) { Size = TuiLength.Auto });
        return this;
    }

    /// <summary>Puts a menu down, closing whatever was.</summary>
    public void OpenAt(int index)
    {
        Close();

        if (index < 0 || index >= _titles.Count)
        {
            return;
        }

        _highlighted = index;
        _titles[index].IsOpen = true;
        _titles[index].Menu.Selected = 0;
        _titles[index].Menu.Dismiss = () => Close();
    }

    /// <summary>Takes down whatever is open. Says whether anything was.</summary>
    public bool Close()
    {
        var was = false;

        foreach (var title in _titles)
        {
            was |= title.IsOpen;
            title.IsOpen = false;
        }

        return was;
    }

    /// <inheritdoc />
    protected override TuiSize MeasureCore(TuiConstraints constraints)
    {
        var width = _titles.Sum(title => title.Measure(constraints).Width) +
            (Gap * Math.Max(0, _titles.Count - 1));

        return constraints.Constrain(new TuiSize(width, 1));
    }

    /// <inheritdoc />
    protected override void ArrangeCore(TuiRect bounds)
    {
        var offset = 0;

        foreach (var title in _titles)
        {
            var width = title.Measure(new TuiConstraints(Math.Max(0, bounds.Width - offset), 1)).Width;

            title.Arrange(new TuiRect(bounds.Left + offset, bounds.Top, width, 1));
            offset += width + Gap;
        }
    }

    /// <inheritdoc />
    public override void Draw(TuiSurface surface)
    {
        surface.Fill(Style);

        foreach (var title in _titles)
        {
            var region = new TuiRect(
                title.Bounds.Left - Bounds.Left,
                title.Bounds.Top - Bounds.Top,
                title.Bounds.Width,
                title.Bounds.Height);

            var slot = surface.Clip(region);

            // Open beats highlighted: while a menu is down, the keyboard is inside it
            // rather than on the bar, and two marks would say otherwise.
            if (title.IsOpen)
            {
                slot.Fill(OpenStyle);
            }
            else if (IsFocused && title == _titles.ElementAtOrDefault(_highlighted))
            {
                slot.Fill(HighlightedStyle);
            }

            title.Draw(slot);
        }
    }

    /// <inheritdoc />
    public override bool OnInput(TuiInputEvent input)
    {
        if (input.IsMouse)
        {
            return ClickedOn(input);
        }

        switch (input.Key.Key)
        {
            case ConsoleKey.LeftArrow: return Walk(-1);
            case ConsoleKey.RightArrow: return Walk(1);
            case ConsoleKey.DownArrow or ConsoleKey.Enter or ConsoleKey.Spacebar:
                OpenAt(_highlighted);
                return true;
            case ConsoleKey.Escape: return Close();
        }

        // A letter opens the menu it names. Only while the bar has the keyboard, so it
        // cannot take a letter away from someone typing in the document below it.
        var typed = input.Key.KeyChar;

        if (char.IsLetterOrDigit(typed) && IndexOf(typed) is { } found)
        {
            OpenAt(found);
            return true;
        }

        return false;
    }

    /// <inheritdoc />
    public override bool Activate()
    {
        OpenAt(_highlighted);
        return true;
    }

    /// <summary>
    /// Moves along the bar, and takes the open menu with it.
    /// </summary>
    /// <remarks>
    /// Walking with a menu down opens the next one rather than closing everything, which
    /// is how a bar is read once it is open: Left and Right are how you look at the others.
    /// </remarks>
    private bool Walk(int direction)
    {
        if (_titles.Count == 0)
        {
            return false;
        }

        var next = ((_highlighted + direction) % _titles.Count + _titles.Count) % _titles.Count;
        var wasOpen = Open is not null;

        _highlighted = next;

        if (wasOpen)
        {
            OpenAt(next);
        }

        return true;
    }

    private int? IndexOf(char letter)
    {
        for (var index = 0; index < _titles.Count; index += 1)
        {
            if (_titles[index].Label.StartsWith(letter.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return null;
    }

    private bool ClickedOn(TuiInputEvent input)
    {
        var mouse = input.Mouse;

        if (mouse.Action != TuiMouseAction.Press || mouse.Button != TuiMouseButton.Left)
        {
            return false;
        }

        for (var index = 0; index < _titles.Count; index += 1)
        {
            if (!_titles[index].Bounds.Contains(mouse.Column, mouse.Row))
            {
                continue;
            }

            // Clicking the open one takes it down, which is what a second click on a menu
            // means everywhere else.
            if (_titles[index].IsOpen)
            {
                Close();
            }
            else
            {
                OpenAt(index);
            }

            return true;
        }

        return false;
    }
}
