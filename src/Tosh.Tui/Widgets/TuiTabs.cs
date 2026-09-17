using Tosh.Tui.Rendering;

namespace Tosh.Tui.Widgets;

/// <summary>One tab: a label and what it shows.</summary>
public sealed record TuiTab(string Label, TuiWidget Content);

/// <summary>
/// Several views in one rectangle, one of them at a time.
/// </summary>
/// <remarks>
/// <para>
/// The container a screen reaches for when it has more to show than room to show it. A
/// script could build one out of a stack and a <c>When</c> predicate per child — and would
/// then own the selection, the labels, the highlighting and the keys, which is four things
/// to get right for a shape every toolkit has (<c>TUI-0002</c>).
/// </para>
/// <para>
/// Only the selected tab is measured, arranged and drawn. A tab that is not on screen costs
/// nothing, which is what makes this worth using for a pane that is expensive to draw — and
/// is why the content is held rather than rebuilt: switching back is free and whatever state
/// the child had is still there.
/// </para>
/// </remarks>
public sealed class TuiTabs : TuiWidget, ITuiCaption
{
    private readonly List<TuiTab> _tabs = [];

    public TuiTabs(IEnumerable<TuiTab>? tabs = null)
    {
        if (tabs is not null)
        {
            _tabs.AddRange(tabs);
        }
    }

    /// <summary>The tabs, in the order they are shown.</summary>
    public IReadOnlyList<TuiTab> Tabs
    {
        get => _tabs;
        set
        {
            ArgumentNullException.ThrowIfNull(value);

            _tabs.Clear();
            _tabs.AddRange(value);
            Selected = Selected;
        }
    }

    /// <summary>Which tab is showing, clamped to what exists.</summary>
    public int Selected
    {
        get;
        set => field = _tabs.Count == 0 ? 0 : Math.Clamp(value, 0, _tabs.Count - 1);
    }

    /// <summary>Blank columns between one label and the next.</summary>
    public int Gap { get; set; } = 2;

    /// <summary>The style of a label that is not showing.</summary>
    public TuiStyle Style { get; set; } = new(Attributes: TuiTextAttributes.Dim);

    /// <summary>The style of the label that is.</summary>
    public TuiStyle SelectedStyle { get; set; } = new(Attributes: TuiTextAttributes.Bold);

    /// <summary>
    /// Whether to rule a line under the labels.
    /// </summary>
    /// <remarks>
    /// On by default: without it the labels read as a row of words sitting above unrelated
    /// content rather than as the handle for it.
    /// </remarks>
    public bool Underline { get; set; } = true;

    /// <summary>Told which tab was chosen, whenever that changes.</summary>
    public Action<int>? Changed { get; set; }

    /// <summary>
    /// What a border around this should call it, overriding the tab's own label.
    /// </summary>
    /// <remarks>
    /// Unset, a frame around the tabs is titled with whichever tab is showing, which is
    /// what the labels already say and what a reader coming back to the screen wants to
    /// read first. Set, it says what it is told and stops tracking.
    /// </remarks>
    public string? Title { get; set; }

    /// <inheritdoc />
    public string? Caption
        => Title ?? (_tabs.Count == 0 ? null : _tabs[Selected].Label);

    /// <summary>Adds a tab. Returns this, so a tree can be written as one expression.</summary>
    public TuiTabs Add(string label, TuiWidget content)
    {
        ArgumentNullException.ThrowIfNull(content);

        _tabs.Add(new TuiTab(label ?? string.Empty, content));

        return this;
    }

    /// <summary>The label of the tab showing, which is what a form reads back.</summary>
    public override object? Value
        => _tabs.Count == 0 ? null : _tabs[Selected].Label;

    /// <inheritdoc />
    /// <remarks>
    /// Every tab's content, not just the selected one. Focus, hit testing and the visibility
    /// pass all walk <c>Children</c>, and a tab that reported only what is showing would
    /// have its other panes disappear from the tree — which is how a binding on a hidden tab
    /// stops being asked and comes back stale when the reader switches to it.
    /// </remarks>
    public override IReadOnlyList<TuiWidget> Children
        => [.. _tabs.Select(tab => tab.Content)];

    /// <summary>Only the tab showing can take the keyboard.</summary>
    public override IReadOnlyList<TuiWidget> FocusChildren
        => _tabs.Count == 0 ? [] : [_tabs[Selected].Content];

    /// <inheritdoc />
    public override bool IsFocusable => _tabs.Count > 1;

    /// <inheritdoc />
    protected override TuiSize MeasureCore(TuiConstraints constraints)
    {
        var header = HeaderHeight;
        var content = _tabs.Count == 0
            ? new TuiSize(0, 0)
            : _tabs[Selected].Content.Measure(
                new TuiConstraints(constraints.MaxWidth, Math.Max(0, constraints.MaxHeight - header)));

        return constraints.Constrain(
            new TuiSize(Math.Max(LabelsWidth(), content.Width), content.Height + header));
    }

    /// <inheritdoc />
    protected override void ArrangeCore(TuiRect bounds)
    {
        if (_tabs.Count == 0)
        {
            return;
        }

        var header = HeaderHeight;

        _tabs[Selected].Content.Arrange(new TuiRect(
            bounds.Left,
            bounds.Top + header,
            bounds.Width,
            Math.Max(0, bounds.Height - header)));
    }

    /// <inheritdoc />
    public override void Draw(TuiSurface surface)
    {
        if (_tabs.Count == 0 || surface.Width <= 0 || surface.Height <= 0)
        {
            return;
        }

        var column = 0;

        for (var index = 0; index < _tabs.Count && column < surface.Width; index += 1)
        {
            var label = _tabs[index].Label;
            var style = index == Selected ? SelectedStyle : Style;

            column += surface.DrawText(column, 0, label, style, surface.Width - column);
            column += Gap;
        }

        if (Underline && surface.Height > 1)
        {
            surface.DrawText(0, 1, new string('─', surface.Width), Style);
        }

        DrawChild(_tabs[Selected].Content, surface);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Left and right, which is what a row of labels looks like it should answer. Not Tab:
    /// that moves the keyboard between panes and a reader who has learned it means "next
    /// widget" should not find it meaning two things on one screen.
    /// </remarks>
    public override bool OnInput(TuiInputEvent input)
    {
        if (!input.IsKey || _tabs.Count < 2)
        {
            return false;
        }

        var moved = input.Key.Key switch
        {
            ConsoleKey.LeftArrow => Selected - 1,
            ConsoleKey.RightArrow => Selected + 1,
            ConsoleKey.Home => 0,
            ConsoleKey.End => _tabs.Count - 1,
            _ => Selected,
        };

        // Wrapping, because a row of tabs is a ring: stopping at the last one means a reader
        // holding right has to work out which way to go back.
        if (input.Key.Key is ConsoleKey.LeftArrow or ConsoleKey.RightArrow)
        {
            moved = (moved + _tabs.Count) % _tabs.Count;
        }

        if (moved == Selected)
        {
            return false;
        }

        Selected = moved;
        Changed?.Invoke(Selected);

        return true;
    }

    /// <summary>The header is the labels, and the rule under them if there is one.</summary>
    private int HeaderHeight => _tabs.Count == 0 ? 0 : Underline ? 2 : 1;

    private int LabelsWidth()
    {
        if (_tabs.Count == 0)
        {
            return 0;
        }

        var total = Gap * (_tabs.Count - 1);

        foreach (var tab in _tabs)
        {
            total += TuiTextMeasure.MeasureWidth(tab.Label);
        }

        return total;
    }
}
