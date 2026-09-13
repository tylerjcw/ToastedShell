using Tosh.Runtime;
using Tosh.Tui;
using Tosh.Tui.Rendering;
using Tosh.Tui.Widgets;

namespace Tosh.Cli.Tui;

/// <summary>
/// The help browser's frame, as a widget tree (<c>TUI-0003</c>).
/// </summary>
/// <remarks>
/// <para>
/// What moves here is the drawing, and only the drawing. The browser's model — which
/// topic, which group, what is collapsed, where history goes — is untouched, and so is
/// every key it answers. That division is the point: a screen that hand-rendered its own
/// lines could not be clicked accurately, could not draw anything over anything else, and
/// repainted in full to move one character.
/// </para>
/// <para>
/// The tree is built once and updated in place. Rebuilding it each frame would throw away
/// the selection and offset the widgets are now keeping, which is the thing that lets the
/// browser stop keeping them itself.
/// </para>
/// </remarks>
internal sealed partial class HelpBrowserScreen
{
    private readonly TuiLines _groupLine = new();
    private readonly TuiLines _searchRow = new();
    private readonly TuiList _sidebarList = new();
    private readonly TuiLines _detailLines = new();
    private readonly TuiLines _footerLine = new();
    private TuiBorder _headerFrame = null!;
    private TuiBorder _listFrame = null!;
    private TuiBorder _detailFrame = null!;
    private TuiStack _panes = null!;
    private TuiStack _tree = null!;

    /// <summary>Assembles the frame. Called from the constructor, once.</summary>
    private HelpBrowserScreen BuildTree()
    {
        _sidebarList.SpanSelector = (item, selected) => SidebarLine(item, selected);

        // The state owns the selection; the list reports where a click landed and the
        // model decides what that means, exactly as the file picker does.
        _sidebarList.SelectionChanged = index => _sidebar.SelectIndex(index);

        _headerFrame = new TuiBorder(
            new TuiStack(TuiOrientation.Vertical)
                .Add(_groupLine, TuiLength.Fixed(1))
                .Add(_searchRow, TuiLength.Fixed(1)),
            "Help Browser")
        {
            Size = TuiLength.Fixed(SearchBoxHeight),
        };

        // A third of the window, never narrower than 24 nor wider than 40. Said once, as
        // one length, rather than clamped per frame and pinned as a fixed size — which is
        // what stopped it being a third of anything the moment the terminal was resized
        // past either bound (`TUI-0019`).
        _listFrame = new TuiBorder(_sidebarList) { Size = "1/3 24..40" };
        _detailFrame = new TuiBorder(_detailLines) { Size = TuiLength.Star() };

        _panes = new TuiStack(TuiOrientation.Horizontal) { Gap = 1, Size = TuiLength.Star() };
        _panes.Add(_listFrame);
        _panes.Add(_detailFrame);

        _tree = new TuiStack(TuiOrientation.Vertical)
            .Add(_headerFrame)
            .Add(_panes)
            .Add(_footerLine, TuiLength.Fixed(1));

        return this;
    }

    /// <summary>Draws the current model into a grid of cells.</summary>
    public TuiFrame Render(TuiSize size)
    {
        var width = Math.Max(60, size.Width);
        var height = Math.Max(14, size.Height);

        var theme = _runtime.Config.Theme.Tui;
        var glyphs = BorderGlyphs(theme.BoxStyle);

        SyncSidebar(Math.Max(1, height - SearchBoxHeight - 1 - 2));

        foreach (var frame in (TuiBorder[])[_headerFrame, _listFrame, _detailFrame])
        {
            frame.Glyphs = glyphs;
            frame.Style = theme.Border.ToStyle();
            frame.TitleStyle = theme.Title.ToStyle();
        }

        _listFrame.Title = GetGroupTitle(_activeGroup);
        _detailFrame.Title = GetDetailTitle();

        _groupLine.Lines = [GroupLine(theme, Math.Max(1, width - 2))];
        _searchRow.Lines = [SearchRow(theme, Math.Max(1, width - 2))];
        _footerLine.Lines = [FooterLine(theme, width)];

        _sidebarList.Items = [.. _sidebar.Items.Cast<object?>()];
        _sidebarList.SelectedIndex = _sidebar.SelectedIndex;

        var buffer = new TuiBuffer(new TuiSize(width, height));
        var bounds = new TuiRect(0, 0, width, height);

        _tree.Measure(TuiConstraints.From(new TuiSize(width, height)));
        _tree.Arrange(bounds);

        // The detail pane wraps to the width it was given, which is only settled once the
        // tree has been arranged — so its lines are built between arrange and draw rather
        // than from a copy of the layout arithmetic kept alongside it.
        var detailInner = Math.Max(1, _detailLines.Bounds.Width);

        _detailLines.Lines = [.. BuildDetailEntries(detailInner)
            .Select(entry => new TuiSpanLine([
                new TuiSpan(
                    TuiRenderHelpers.TrimOrPadPlain(entry.Text, detailInner),
                    GetDetailStyle(entry.Kind, theme).ToStyle()),
            ]))];

        _tree.Draw(new TuiSurface(buffer, bounds));

        return new TuiFrame(buffer);
    }

    /// <summary>One sidebar row: the gutter marker, then whatever the entry is made of.</summary>
    private TuiSpanLine SidebarLine(object? item, bool isSelected)
    {
        var line = new TuiSpanLine();
        var theme = _runtime.Config.Theme.Tui;

        line.Add(isSelected ? "› " : "  ", (isSelected ? theme.SelectedGutter : theme.Meta).ToStyle());

        if (item is HelpBrowserListEntry entry)
        {
            foreach (var (text, style) in BuildSidebarContentSegments(entry, isSelected, theme))
            {
                line.Add(text, style.ToStyle());
            }
        }

        return line;
    }

    /// <summary>The group tabs, or the active group's name when they do not fit.</summary>
    private TuiSpanLine GroupLine(ToshTuiThemeConfig theme, int width)
    {
        var groups = (HelpBrowserGroup[])
        [
            HelpBrowserGroup.All,
            HelpBrowserGroup.ToastedShell,
            HelpBrowserGroup.ToastScript,
            HelpBrowserGroup.Clr,
        ];

        var line = new TuiSpanLine();

        for (var index = 0; index < groups.Length; index += 1)
        {
            if (index > 0)
            {
                line.Add("  ", theme.Meta.ToStyle());
            }

            var group = groups[index];
            line.Add(BuildGroupLabel(group), (group == _activeGroup ? theme.Title : theme.Meta).ToStyle());
        }

        if (line.Width <= width)
        {
            return line;
        }

        return new TuiSpanLine()
            .Add(TuiRenderHelpers.TrimOrPadPlain(GetGroupTitle(_activeGroup), width), theme.Meta.ToStyle());
    }

    private TuiSpanLine SearchRow(ToshTuiThemeConfig theme, int width)
    {
        var label = $"{(_focus == HelpBrowserFocus.Search ? "Search*" : "Search")}: ";

        return new TuiSpanLine()
            .Add(label, theme.SearchLabel.ToStyle())
            .Add(TuiRenderHelpers.TrimOrPadPlain(_query, Math.Max(0, width - label.Length)).TrimEnd(), theme.SearchInput.ToStyle());
    }

    private TuiSpanLine FooterLine(ToshTuiThemeConfig theme, int width)
    {
        var focus = _focus.ToString().ToLowerInvariant();

        var text = $"focus:{focus}  F1-F4 groups  {_shortcuts.Describe()}  Enter open/toggle  i insert  Left up  1-9 related";

        return new TuiSpanLine().Add(TuiRenderHelpers.TrimOrPadPlain(text, width), theme.Footer.ToStyle());
    }

    private static TuiBorderGlyphs BorderGlyphs(ToshTableBoxStyle style) => style switch
    {
        ToshTableBoxStyle.Square => TuiBorderGlyphs.Square,
        ToshTableBoxStyle.Heavy => TuiBorderGlyphs.Heavy,
        ToshTableBoxStyle.Double => TuiBorderGlyphs.Double,
        ToshTableBoxStyle.Ascii => TuiBorderGlyphs.Ascii,
        _ => TuiBorderGlyphs.Rounded,
    };
}
