using System.Text;
using Tosh.Core;
using Tosh.Tui;
using Tosh.Tui.Requests;

namespace Tosh.Cli.Tui;

internal sealed class TuiPickScreen : ITuiScreen
{
    private readonly TuiPickRequest _request;
    private readonly ObjectFormatter? _formatter;
    private readonly TuiListState<object?> _list = new();
    private readonly TuiTextInputState _search = new();
    private readonly HashSet<int> _selectedIndices = new();
    private IReadOnlyList<object?> _filteredItems;
    private bool _searchActive;
    private int _headerLines;
    private int _listPageSize;


    public TuiPickScreen(TuiPickRequest request, ObjectFormatter? formatter = null)
    {
        _request = request;
        _formatter = formatter;
        _filteredItems = request.Items;
    }

    public TuiScreenOutcome? Outcome { get; private set; }

    public TuiFrame Render(TuiSize size)
    {
        var sb = new StringBuilder();
        var width = size.Width;
        var height = size.Height;

        // Header
        var title = _request.Prompt ?? "Select an item";

        if (_request.MultiSelect)
        {
            title += $" ({_selectedIndices.Count} selected)";
        }

        sb.AppendLine(title.Length > width ? title[..width] : title);

        // Search bar
        if (_searchActive)
        {
            var searchLine = $"Search: {_search.RenderWithCursor()}";
            sb.AppendLine(searchLine.Length > width ? searchLine[..width] : searchLine);
        }

        sb.AppendLine(new string('─', Math.Min(width, 80)));

        // List area
        var headerLines = _searchActive ? 3 : 2;
        var footerLines = 2;
        var pageSize = Math.Max(1, height - headerLines - footerLines);
        _headerLines = headerLines;
        _listPageSize = pageSize;
        _list.SetItems(_filteredItems, pageSize);
        var range = _list.Scroll.GetVisibleRange();

        for (var row = 0; row < range.Length; row++)
        {
            var itemIndex = range.Start + row;
            var item = _filteredItems[itemIndex];
            var isHighlighted = itemIndex == _list.SelectedIndex;
            var isSelected = _request.MultiSelect && _selectedIndices.Contains(GetOriginalIndex(item));

            var prefix = isHighlighted ? ">" : " ";

            if (_request.MultiSelect)
            {
                prefix += isSelected ? " [x] " : " [ ] ";
            }
            else
            {
                prefix += " ";
            }

            var label = FormatItem(item);
            var line = prefix + label;
            sb.AppendLine(line.Length > width ? line[..width] : line);
        }

        // Footer
        sb.AppendLine();
        var help = _request.MultiSelect
            ? "Up/Down: navigate | Space: toggle | Enter: confirm | /: search | Esc: cancel"
            : "Up/Down: navigate | Enter: select | /: search | Esc: cancel";
        sb.Append(help.Length > width ? help[..width] : help);

        return new TuiFrame(sb.ToString());
    }

    public TuiScreenResult HandleInput(TuiInputEvent input)
    {
        if (input.IsKey)
            return HandleKey(input.Key);

        var mouse = input.Mouse;

        // Scroll wheel navigates the list
        if (mouse.Action == TuiMouseAction.Scroll)
        {
            if (mouse.Button == TuiMouseButton.ScrollUp)
                _list.MovePrevious();
            else if (mouse.Button == TuiMouseButton.ScrollDown)
                _list.MoveNext();

            return TuiScreenResult.Continue;
        }

        // Click on a list item to select it
        if (mouse.Action == TuiMouseAction.Press && mouse.Button == TuiMouseButton.Left)
        {
            var listRow = mouse.Row - _headerLines;
            var range = _list.Scroll.GetVisibleRange();

            if (listRow >= 0 && listRow < range.Length)
            {
                var itemIndex = range.Start + listRow;
                _list.SelectIndex(itemIndex);

                return TuiScreenResult.Continue;
            }
        }

        return TuiScreenResult.Continue;
    }

    public TuiScreenResult HandleKey(ConsoleKeyInfo key)
    {
        if (_searchActive)
        {
            if (key.Key == ConsoleKey.Escape)
            {
                _searchActive = false;
                _search.SetText(null);
                _filteredItems = _request.Items;
                return TuiScreenResult.Continue;
            }

            if (key.Key == ConsoleKey.Enter)
            {
                _searchActive = false;
                return TuiScreenResult.Continue;
            }

            _search.HandleKey(key);
            ApplyFilter();
            return TuiScreenResult.Continue;
        }

        switch (key.Key)
        {
            case ConsoleKey.UpArrow:
                _list.MovePrevious();
                return TuiScreenResult.Continue;
            case ConsoleKey.DownArrow:
                _list.MoveNext();
                return TuiScreenResult.Continue;
            case ConsoleKey.PageUp:
                _list.PageUp();
                return TuiScreenResult.Continue;
            case ConsoleKey.PageDown:
                _list.PageDown();
                return TuiScreenResult.Continue;
            case ConsoleKey.Home:
                _list.Home();
                return TuiScreenResult.Continue;
            case ConsoleKey.End:
                _list.End();
                return TuiScreenResult.Continue;
            case ConsoleKey.Spacebar when _request.MultiSelect:
                ToggleSelection();
                return TuiScreenResult.Continue;
            case ConsoleKey.Enter:
                Commit();
                return TuiScreenResult.Exit;
            case ConsoleKey.Escape:
            case ConsoleKey.Q:
                Cancel();
                return TuiScreenResult.Exit;
        }

        if (key.KeyChar == '/')
        {
            _searchActive = true;
            return TuiScreenResult.Continue;
        }

        return TuiScreenResult.Continue;
    }

    private void ToggleSelection()
    {
        if (!_list.TryGetSelected(out var item))
        {
            return;
        }

        var originalIndex = GetOriginalIndex(item);

        if (!_selectedIndices.Remove(originalIndex))
        {
            _selectedIndices.Add(originalIndex);
        }
    }

    private void Commit()
    {

        if (_request.MultiSelect)
        {
            var selected = _selectedIndices
                .OrderBy(i => i)
                .Select(i => _request.Items[i])
                .ToArray();

            Outcome = new TuiScreenOutcome
            {
                Selected = selected,
                Cancelled = false,
            };
        }
        else
        {
            if (_list.TryGetSelected(out var item))
            {
                Outcome = new TuiScreenOutcome
                {
                    Selected = [item],
                    Cancelled = false,
                };
            }
            else
            {
                Cancel();
            }
        }
    }

    private void Cancel()
    {
        Outcome = new TuiScreenOutcome { Cancelled = true };
    }

    private string FormatItem(object? item)
    {
        if (item is null)
        {
            return "(null)";
        }

        var type = item.GetType();
        var flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase;

        if (_request.DisplayProperty is not null)
        {
            var prop = type.GetProperty(_request.DisplayProperty, flags);

            if (prop is not null)
            {
                return prop.GetValue(item)?.ToString() ?? string.Empty;
            }
        }

        // Try display profile rendering for known types
        if (_request.DisplayProperty is null && _formatter is not null)
        {
            var options = new ObjectFormattingOptions(ObjectRenderStyle.Compact);

            if (_formatter.TryRenderProfile(item, options, DisplaySurface.Root, out var text) && !string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        // For non-primitive types without a display property, try well-known names
        if (_request.DisplayProperty is null && !type.IsPrimitive && type != typeof(string) && !type.IsEnum)
        {
            var label = TryGetPropertyValue(item, type, "Name", flags)
                     ?? TryGetPropertyValue(item, type, "DisplayName", flags)
                     ?? TryGetPropertyValue(item, type, "Title", flags)
                     ?? TryGetPropertyValue(item, type, "Label", flags);

            if (label is not null)
            {
                return label;
            }
        }

        return item.ToString() ?? string.Empty;
    }

    private static string? TryGetPropertyValue(object item, Type type, string name, System.Reflection.BindingFlags flags)
    {
        var prop = type.GetProperty(name, flags);
        return prop is not null ? prop.GetValue(item)?.ToString() : null;
    }

    private int GetOriginalIndex(object? item)
    {
        for (var i = 0; i < _request.Items.Count; i++)
        {
            if (ReferenceEquals(_request.Items[i], item) || Equals(_request.Items[i], item))
            {
                return i;
            }
        }

        return -1;
    }

    private void ApplyFilter()
    {
        var query = _search.Text.Trim();

        if (query.Length == 0)
        {
            _filteredItems = _request.Items;
            return;
        }

        _filteredItems = _request.Items
            .Where(item => FormatItem(item).Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }
}
