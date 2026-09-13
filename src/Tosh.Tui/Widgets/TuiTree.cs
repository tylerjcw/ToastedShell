using Tosh.Runtime;
using Tosh.Tui.Rendering;

namespace Tosh.Tui.Widgets;

/// <summary>The characters a tree draws its guides and markers with.</summary>
/// <param name="Expanded">Shown on a node whose children are visible.</param>
/// <param name="Collapsed">Shown on a node with children that are not.</param>
/// <param name="Leaf">Shown on a node with nothing under it.</param>
public readonly record struct TuiTreeGlyphs(
    string Expanded,
    string Collapsed,
    string Leaf,
    string Branch,
    string LastBranch,
    string Trunk,
    string Blank)
{
    /// <summary>What both browsers draw today.</summary>
    public static TuiTreeGlyphs Default => new("▾", "▸", "─", "├", "└", "│ ", "  ");

    /// <summary>
    /// Indentation and markers, with no connectors drawn between them.
    /// </summary>
    /// <remarks>
    /// Named for the theme setting it answers: <c>Theme.Tui.TreeStyle</c> is
    /// <c>Clean</c> or <c>Dense</c>, and this is what <c>Clean</c> asks for.
    /// </remarks>
    public static TuiTreeGlyphs Clean => new("▾", "▸", " ", " ", " ", "  ", "  ");

    /// <summary>Drawable where the terminal cannot manage box characters.</summary>
    public static TuiTreeGlyphs Ascii => new("-", "+", " ", "|", "`", "| ", "  ");
}

/// <summary>One visible row of a tree: the node, and where it sits.</summary>
/// <param name="Depth">How many ancestors it has, counting from the visible root.</param>
/// <param name="IsLast">Whether it is the last child of its parent, which decides its connector.</param>
public sealed record TuiTreeRow(object? Node, int Depth, bool IsLast, bool HasChildren, bool IsExpanded);

/// <summary>
/// A tree of anything, drawn with guides and opened a node at a time.
/// </summary>
/// <remarks>
/// <para>
/// The CLR explorer draws a namespace tree; the config browser draws a settings tree. Both
/// build the rows themselves, flatten them into a list, and keep their own set of expanded
/// paths. A tree is a control, and a shell that walks filesystems, configuration and object
/// graphs has them everywhere — but a script could not show one at all (<c>TUI-0018</c>).
/// </para>
/// <para>
/// It does not own the data. A root and a function from node to children is the whole of
/// what it needs, and children are asked for when a node opens rather than when the tree is
/// built — which is not a nicety: the CLR explorer's root has every loaded assembly under
/// it and its leaves are every type in each.
/// </para>
/// <code>
/// var tree = new TuiTree($root)
///
/// $tree.ChildrenOf = func(node) { return $node.Children }
/// $tree.Display  = func(node) { return $node.Name }
/// </code>
/// <para>
/// Expansion is keyed by a stable id rather than by row index, for the reason
/// <see cref="TuiList"/>'s ticks are: the indices of a filtered tree mean something
/// different after every keystroke, and state keyed to them moves to whatever took their
/// place.
/// </para>
/// </remarks>
public sealed class TuiTree : TuiWidget
{
    private readonly HashSet<string> _expanded = new(StringComparer.Ordinal);
    private IReadOnlyList<TuiTreeRow> _rows = [];
    private int _selectedIndex;
    private int _offset;

    public TuiTree(object? root = null)
    {
        Root = root;
    }

    /// <summary>The node everything hangs from.</summary>
    public object? Root { get; set; }

    /// <summary>
    /// What is under a node. Enumerated when the node opens, not before.
    /// </summary>
    /// <remarks>
    /// Named for how it reads at a call site — <c>$tree.ChildrenOf = func(node) => …</c> —
    /// and because <see cref="TuiWidget.Children"/> already means something else on every
    /// widget: the widgets inside this one. Two properties called <c>Children</c> on one
    /// type is a member the CLR cannot bind, which is how this was found.
    /// </remarks>
    public Func<object?, IEnumerable<object?>>? ChildrenOf { get; set; }

    /// <summary>
    /// Whether a node has anything under it, when that is cheaper to answer than to find out.
    /// </summary>
    /// <remarks>
    /// Without this the tree asks <see cref="ChildrenOf"/> for the first item of every
    /// <em>visible</em> node, which is what tells a leaf from an unopened branch. That is
    /// one level deep and only for rows on screen, so it stays lazy in the way that
    /// matters — but a caller who already knows, from a count it is holding or a flag on
    /// the node, should say so rather than make the tree find out.
    /// </remarks>
    public Func<object?, bool>? HasChildren { get; set; }

    /// <summary>
    /// A node's stable identity, which expansion is remembered against.
    /// </summary>
    /// <remarks>
    /// Defaults to the node's own <c>ToString()</c>, which is right when nodes are paths or
    /// names and wrong when they are values that repeat. Anything walking a real tree
    /// should say what a node's identity is.
    /// </remarks>
    public Func<object?, string>? Identify { get; set; }

    /// <summary>A node's label.</summary>
    public Func<object?, string>? Display { get; set; }

    /// <summary>Renders a whole row, for a caller who wants more than a label.</summary>
    /// <remarks>
    /// Given this, the tree draws no guides or marker of its own: a row that says how to
    /// draw itself draws itself entirely, as <see cref="TuiList.SpanSelector"/> does. The
    /// row it is handed says how deep it is and what is under it.
    /// </remarks>
    public Func<TuiTreeRow, bool, TuiSpanLine>? LineSelector { get; set; }

    /// <summary>Whether the root is drawn, or only what is under it.</summary>
    public bool ShowRoot { get; set; } = true;

    /// <summary>
    /// The glyph set a tree uses when its own is not set. Installed by the host.
    /// </summary>
    /// <remarks>
    /// A tree cannot read the user's configuration — it is a widget, and the widgets know
    /// nothing about a shell. The host that does installs this, the same way it installs
    /// <see cref="TuiTable.ColumnSource"/>, and a tree then follows
    /// <c>Theme.Tui.TreeStyle</c> without being told to. A host that installs nothing gets
    /// <see cref="TuiTreeGlyphs.Default"/>.
    /// </remarks>
    public static Func<TuiTreeGlyphs>? DefaultGlyphs { get; set; }

    /// <summary>
    /// The characters the guides and markers are drawn with.
    /// </summary>
    /// <remarks>
    /// Asked for on every draw rather than captured when the tree was built, so a theme
    /// changed while a screen is up takes effect on the next frame.
    /// </remarks>
    public TuiTreeGlyphs? Glyphs { get; set; }

    /// <summary>The glyph set actually used to draw, once defaults have been applied.</summary>
    private TuiTreeGlyphs Marks => Glyphs ?? DefaultGlyphs?.Invoke() ?? TuiTreeGlyphs.Default;

    /// <summary>How a row is drawn.</summary>
    public TuiStyle Style { get; set; }

    /// <summary>How the selected row is drawn.</summary>
    public TuiStyle SelectedStyle { get; set; } = new(Attributes: TuiTextAttributes.Bold);

    /// <summary>How the guides and markers are drawn.</summary>
    public TuiStyle GuideStyle { get; set; } = new(Attributes: TuiTextAttributes.Dim);

    /// <summary>Draws a bar down the right edge saying where in the tree you are.</summary>
    public bool Scrollbar { get; set; }

    /// <summary>Raised when the selection moves.</summary>
    public Action<object?>? SelectionChanged { get; set; }

    /// <summary>Raised when a leaf is chosen with Enter. A branch toggles instead.</summary>
    public Action<object?>? Activated { get; set; }

    /// <summary>Raised when a node is opened or closed.</summary>
    public Action<object?, bool>? Toggled { get; set; }

    /// <inheritdoc />
    public override bool IsFocusable { get; } = true;

    /// <inheritdoc />
    public override object? Value => SelectedNode;

    /// <summary>The rows currently visible, in order.</summary>
    public IReadOnlyList<TuiTreeRow> Rows => _rows;

    /// <summary>Which row is selected.</summary>
    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            var clamped = _rows.Count == 0 ? 0 : Math.Clamp(value, 0, _rows.Count - 1);

            if (clamped == _selectedIndex)
            {
                EnsureVisible();
                return;
            }

            _selectedIndex = clamped;
            EnsureVisible();
            SelectionChanged?.Invoke(SelectedNode);
        }
    }

    /// <summary>The selected node, or null when the tree is empty.</summary>
    public object? SelectedNode => _selectedIndex < _rows.Count ? _rows[_selectedIndex].Node : null;

    /// <summary>The first row shown.</summary>
    public int Offset => _offset;

    /// <summary>Whether a node's children are showing.</summary>
    public bool IsExpanded(object? node) => _expanded.Contains(KeyOf(node));

    /// <summary>Opens a node.</summary>
    public void Expand(object? node) => SetExpanded(node, expanded: true);

    /// <summary>Closes a node.</summary>
    public void Collapse(object? node) => SetExpanded(node, expanded: false);

    /// <summary>Opens a node if it is closed, and closes it if it is open.</summary>
    public void Toggle(object? node) => SetExpanded(node, !IsExpanded(node));

    /// <summary>Opens every node on the way to one, so it can be seen.</summary>
    public void Reveal(IEnumerable<object?> path)
    {
        ArgumentNullException.ThrowIfNull(path);

        foreach (var node in path)
        {
            _expanded.Add(KeyOf(node));
        }

        Rebuild();
    }

    /// <summary>Re-reads the visible rows. Call after the data underneath has changed.</summary>
    public void Rebuild()
    {
        var previous = SelectedNode;
        var rows = new List<TuiTreeRow>();

        if (ShowRoot)
        {
            Walk(Root, depth: 0, isLast: true, rows);
        }
        else if (IsExpandedOrRoot(Root))
        {
            AddChildren(Root, depth: 0, rows);
        }

        _rows = rows;

        // The selection follows the node, not the row number: opening something above it
        // must not move it somewhere else.
        var at = previous is null ? -1 : rows.FindIndex(row => Same(row.Node, previous));

        _selectedIndex = at >= 0 ? at : Math.Clamp(_selectedIndex, 0, Math.Max(0, rows.Count - 1));
        EnsureVisible();
    }

    /// <inheritdoc />
    protected override TuiSize MeasureCore(TuiConstraints constraints)
    {
        EnsureRows();

        var width = _rows.Count == 0 ? 0 : _rows.Max(row => Line(row, selected: false).Width);

        return constraints.Constrain(new TuiSize(width, _rows.Count));
    }

    /// <inheritdoc />
    protected override void ArrangeCore(TuiRect bounds)
    {
        EnsureRows();
        EnsureVisible();
    }

    /// <inheritdoc />
    public override void Draw(TuiSurface outer)
    {
        EnsureRows();

        var surface = Scrollbar && _rows.Count > outer.Height
            ? outer.Clip(new TuiRect(0, 0, Math.Max(0, outer.Width - 1), outer.Height))
            : outer;

        for (var row = 0; row < surface.Height; row += 1)
        {
            var index = _offset + row;

            if (index >= _rows.Count)
            {
                break;
            }

            Line(_rows[index], index == _selectedIndex).Draw(surface, row);
        }

        if (Scrollbar)
        {
            TuiScrollbar.DrawVertical(outer, _offset, _rows.Count, GuideStyle, GuideStyle);
        }
    }

    /// <inheritdoc />
    public override bool OnInput(TuiInputEvent input)
    {
        EnsureRows();

        if (!input.IsKey)
        {
            return Click(input.Mouse);
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
            case ConsoleKey.End: SelectedIndex = _rows.Count - 1; return true;

            case ConsoleKey.RightArrow:
                // Right opens; on something already open it steps into it, which is what
                // every tree does and what a reader's hand expects.
                if (Current is not { HasChildren: true } right)
                {
                    return false;
                }

                if (right.IsExpanded)
                {
                    SelectedIndex += 1;
                }
                else
                {
                    Expand(right.Node);
                }

                return true;

            case ConsoleKey.LeftArrow:
                // Left closes; on something already closed it steps out to the parent.
                if (Current is { HasChildren: true, IsExpanded: true } left)
                {
                    Collapse(left.Node);
                    return true;
                }

                return SelectParent();

            case ConsoleKey.Enter:
                if (Current is { HasChildren: true } branch)
                {
                    Toggle(branch.Node);
                    return true;
                }

                if (Activated is null)
                {
                    return false;
                }

                Activated(SelectedNode);
                return true;

            default:
                return false;
        }
    }

    private TuiTreeRow? Current => _selectedIndex < _rows.Count ? _rows[_selectedIndex] : null;

    private bool SelectParent()
    {
        if (Current is not { } current || current.Depth == 0)
        {
            return false;
        }

        for (var index = _selectedIndex - 1; index >= 0; index -= 1)
        {
            if (_rows[index].Depth < current.Depth)
            {
                SelectedIndex = index;
                return true;
            }
        }

        return false;
    }

    private bool Click(TuiMouseEvent mouse)
    {
        if (!Bounds.Contains(mouse.Column, mouse.Row))
        {
            return false;
        }

        if (mouse.Action == TuiMouseAction.Scroll)
        {
            SelectedIndex += mouse.Button == TuiMouseButton.ScrollUp ? -1 : 1;
            return true;
        }

        if (mouse.Action != TuiMouseAction.Press || mouse.Button != TuiMouseButton.Left)
        {
            return false;
        }

        SelectedIndex = _offset + mouse.Row - Bounds.Top;
        return true;
    }

    /// <summary>Builds the rows if nothing has yet.</summary>
    private void EnsureRows()
    {
        if (_rows.Count == 0 && Root is not null)
        {
            Rebuild();
        }
    }

    private void SetExpanded(object? node, bool expanded)
    {
        var key = KeyOf(node);
        var changed = expanded ? _expanded.Add(key) : _expanded.Remove(key);

        if (!changed)
        {
            return;
        }

        Rebuild();
        Toggled?.Invoke(node, expanded);
    }

    private void Walk(object? node, int depth, bool isLast, List<TuiTreeRow> into)
    {
        var expanded = IsExpanded(node);
        var children = expanded ? Enumerate(node) : null;

        // A closed node is asked whether it has a first child, not for all of them: that
        // is what tells a leaf from an unopened branch, and drawing every leaf with a
        // collapse marker beside it is worse than the one lookahead costs.
        var branches = children is not null ? children.Count > 0 : HasAny(node);

        into.Add(new TuiTreeRow(node, depth, isLast, branches, expanded));

        if (children is null)
        {
            return;
        }

        for (var index = 0; index < children.Count; index += 1)
        {
            Walk(children[index], depth + 1, index == children.Count - 1, into);
        }
    }

    private void AddChildren(object? node, int depth, List<TuiTreeRow> into)
    {
        var children = Enumerate(node);

        for (var index = 0; index < children.Count; index += 1)
        {
            Walk(children[index], depth, index == children.Count - 1, into);
        }
    }

    private IReadOnlyList<object?> Enumerate(object? node)
        => ChildrenOf is null ? [] : [.. ChildrenOf(node)];

    /// <summary>Whether a node has a first child, without enumerating the rest.</summary>
    private bool HasAny(object? node)
    {
        if (HasChildren is not null)
        {
            return HasChildren(node);
        }

        return ChildrenOf is not null && ChildrenOf(node).Any();
    }

    /// <summary>A hidden root is always open, or nothing would ever be drawn.</summary>
    private bool IsExpandedOrRoot(object? node) => !ShowRoot || IsExpanded(node);

    private TuiSpanLine Line(TuiTreeRow row, bool selected)
    {
        if (LineSelector is not null)
        {
            return LineSelector(row, selected);
        }

        var line = new TuiSpanLine();

        // The guides. Every level but the last is a trunk or a blank; whether it is one or
        // the other depends on an ancestor being the last of its siblings, which the walk
        // above cannot know for a row it has already emitted — so the connector on this row
        // carries that and the trunks are drawn plain.
        for (var level = 1; level < row.Depth; level += 1)
        {
            line.Add(Marks.Trunk, GuideStyle);
        }

        if (row.Depth > 0)
        {
            line.Add(row.IsLast ? Marks.LastBranch : Marks.Branch, GuideStyle);
        }

        line.Add(
            row.HasChildren ? (row.IsExpanded ? Marks.Expanded : Marks.Collapsed) : Marks.Leaf,
            GuideStyle);

        line.Add(" ", GuideStyle);
        line.Add(Label(row.Node), selected ? SelectedStyle : Style);

        return line;
    }

    private string Label(object? node)
        => Display is not null
            ? Display(node) ?? string.Empty
            : node?.ToString() ?? string.Empty;

    private string KeyOf(object? node)
        => Identify is not null ? Identify(node) ?? string.Empty : node?.ToString() ?? string.Empty;

    private bool Same(object? left, object? right)
        => ReferenceEquals(left, right) || string.Equals(KeyOf(left), KeyOf(right), StringComparison.Ordinal);

    private void EnsureVisible()
    {
        var page = Math.Max(1, Bounds.Height);

        _offset = Math.Clamp(_offset, 0, Math.Max(0, _rows.Count - page));

        if (_selectedIndex < _offset)
        {
            _offset = _selectedIndex;
        }
        else if (_selectedIndex >= _offset + page)
        {
            _offset = _selectedIndex - page + 1;
        }
    }
}
