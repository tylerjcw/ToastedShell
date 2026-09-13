using Tosh.Runtime;
using Tosh.Tui;
using Tosh.Tui.Rendering;
using Tosh.Tui.Widgets;

namespace Tosh.Cli.Tui;

/// <summary>
/// The config browser's frame, as a widget tree (<c>TUI-0003</c>).
/// </summary>
/// <remarks>
/// <para>
/// The same move the help browser made, and the last screen still building its frame out
/// of concatenated strings. What changes is the drawing: the schema, the staging, the
/// eight editors, the validation and the live previews are untouched, and so is every key
/// the browser answers.
/// </para>
/// <para>
/// The tree pane stays a <see cref="TuiList"/> over rows the model has already flattened,
/// rather than a <see cref="TuiTree"/>. The model owns expansion, filtering and
/// selection-by-path and shares them with the editors; moving that into the widget is a
/// second change, and doing it in the same breath as the port would leave neither
/// verifiable against the recorded sessions.
/// </para>
/// </remarks>
internal sealed partial class ConfigBrowserScreen
{
    private readonly TuiLines _searchRow = new();
    private readonly TuiList _treeList = new();
    private readonly TuiLines _detailLines = new();
    private readonly TuiLines _footerLine = new();
    private TuiBorder _headerFrame = null!;
    private TuiBorder _treeFrame = null!;
    private TuiBorder _detailFrame = null!;
    private TuiStack _panes = null!;
    private TuiStack _tui = null!;

    /// <summary>Assembles the frame. Called from the constructor, once.</summary>
    private void BuildTree()
    {
        _treeList.SpanSelector = (item, selected) => TreeLine(item, selected);
        _treeList.SelectionChanged = index => _tree.SelectIndex(index);

        _headerFrame = new TuiBorder(_searchRow, "Config Browser") { Size = TuiLength.Fixed(SearchBoxHeight) };

        // A third of the frame, but never so narrow that a setting's name is unreadable and
        // never so wide that the detail pane has nowhere to put a preview.
        _treeFrame = new TuiBorder(_treeList, "Configuration") { Size = TuiLength.Ratio(1, 3).AtLeast(28) };
        _detailFrame = new TuiBorder(_detailLines) { Size = TuiLength.Star() };

        _panes = new TuiStack(TuiOrientation.Horizontal) { Gap = 1, Size = TuiLength.Star() };
        _panes.Add(_treeFrame);
        _panes.Add(_detailFrame);

        _tui = new TuiStack(TuiOrientation.Vertical)
            .Add(_headerFrame)
            .Add(_panes)
            .Add(_footerLine, TuiLength.Fixed(1));
    }

    /// <summary>Draws the current model into a grid of cells.</summary>
    public TuiFrame Render(TuiSize size)
    {
        var width = Math.Max(20, size.Width);
        var height = Math.Max(8, size.Height);

        var theme = _runtime.Config.Theme.Tui;

        foreach (var frame in (TuiBorder[])[_headerFrame, _treeFrame, _detailFrame])
        {
            frame.Glyphs = BorderGlyphs(theme.BoxStyle);
            frame.Style = theme.Border.ToStyle();
            frame.TitleStyle = theme.Title.ToStyle();
        }

        _detailFrame.Title = DetailTitle();

        SyncTree(Math.Max(1, height - SearchBoxHeight - 1 - 2));

        _searchRow.Lines = [SearchRow(theme, Math.Max(1, width - 2))];
        _footerLine.Lines = [new TuiSpanLine().Add(
            TuiRenderHelpers.TrimOrPadPlain(FooterText(), width),
            theme.Footer.ToStyle())];

        _treeList.Items = [.. _tree.Items.Cast<object?>()];
        _treeList.SelectedIndex = _tree.SelectedIndex;

        var buffer = new TuiBuffer(new TuiSize(width, height));
        var bounds = new TuiRect(0, 0, width, height);

        _tui.Measure(TuiConstraints.From(new TuiSize(width, height)));
        _tui.Arrange(bounds);

        // The detail pane wraps to the width it was given, which is only settled once the
        // tree has been arranged.
        var inner = Math.Max(1, _detailLines.Bounds.Width);

        _detailLines.Lines = [.. BuildDetailEntries(inner)
            .Select(entry => new TuiSpanLine([
                new TuiSpan(
                    TuiRenderHelpers.TrimOrPadPlain(entry.Text, inner),
                    GetDetailStyle(entry.Kind, theme).ToStyle()),
            ]))];

        _tui.Draw(new TuiSurface(buffer, bounds));

        return new TuiFrame(buffer);
    }

    /// <summary>What the detail pane is currently showing.</summary>
    private string DetailTitle()
        => _confirmDialog.IsOpen
            ? _confirmDialog.Title
            : _pathEditor.IsBrowsing
                ? "Filesystem Picker"
                : _tree.TryGetSelected(out var selected)
                    ? selected.Node.DisplayName
                    : "Details";

    /// <summary>One row of the settings tree, already indented and marked by the model.</summary>
    private TuiSpanLine TreeLine(object? item, bool isSelected)
    {
        var theme = _runtime.Config.Theme.Tui;
        var line = new TuiSpanLine();

        if (item is not ConfigBrowserListEntry entry)
        {
            return line;
        }

        var style = entry.Node.Kind == ConfigBrowserNodeKind.Group
            ? TuiRenderHelpers.MergeListStyles(theme.SectionHeading, theme.SelectedItem, isSelected, preserveForeground: true)
            : TuiRenderHelpers.MergeListStyles(theme.ListItem, theme.SelectedItem, isSelected, preserveForeground: false);

        return line.Add(entry.Label, style.ToStyle());
    }

    private TuiSpanLine SearchRow(ToshTuiThemeConfig theme, int width)
    {
        var label = $"{(_focus == ConfigBrowserFocus.Search ? "Search*" : "Search")}: ";

        return new TuiSpanLine()
            .Add(label, theme.SearchLabel.ToStyle())
            .Add(
                TuiRenderHelpers.TrimOrPadPlain(_query, Math.Max(0, width - label.Length)).TrimEnd(),
                theme.SearchInput.ToStyle());
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
