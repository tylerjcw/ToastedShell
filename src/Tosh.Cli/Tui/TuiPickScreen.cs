using System.Reflection;
using Tosh.Runtime;
using Tosh.Tui;
using Tosh.Tui.Rendering;
using Tosh.Tui.Requests;
using Tosh.Tui.Widgets;

namespace Tosh.Cli.Tui;

/// <summary>
/// The picker behind <c>tui pick</c> and <c>tui filter</c>.
/// </summary>
/// <remarks>
/// <para>
/// Built from widgets (<c>TUI-0002</c>). The screen owns which items are ticked and what
/// the search says; the list draws and moves, the scroll container keeps the selection
/// visible, and the text field owns the search box.
/// </para>
/// <para>
/// Ticks are kept against the unfiltered collection rather than by row, because a row
/// means something different after every keystroke in the search box — tick two items,
/// type a letter, and index-based ticks land on whatever took their place.
/// </para>
/// </remarks>
internal sealed class TuiPickScreen : ITuiScreen
{
    private readonly TuiPickRequest _request;
    private readonly ObjectFormatter? _formatter;
    private readonly HashSet<int> _ticked = [];
    private readonly TuiList _list;
    private readonly TuiTextField _search;
    private readonly TuiBorder _frame;
    private readonly TuiTextWidget _footer;
    private TuiStack _content = new();
    private TuiFocus _focus;
    private bool _searchActive;

    public TuiPickScreen(TuiPickRequest request, ObjectFormatter? formatter = null)
    {
        _request = request;
        _formatter = formatter;

        _list = new TuiList(request.Items)
        {
            MultiSelect = request.MultiSelect,
            DisplaySelector = FormatItem,
            IsChecked = item => _ticked.Contains(OriginalIndexOf(item)),
            CheckedToggled = Toggle,
            Activated = _ => Commit(),
        };


        _search = new TuiTextField
        {
            Placeholder = "type to filter",
            Changed = _ => ApplyFilter(),
            Submitted = _ => CloseSearch(keepQuery: true),
            Cancelled = () => CloseSearch(keepQuery: false),
        };

        _frame = new TuiBorder { TitleStyle = new TuiStyle(Attributes: TuiTextAttributes.Bold) };
        _footer = new TuiTextWidget { Style = new TuiStyle(Attributes: TuiTextAttributes.Dim) };

        // `tui filter` is this screen with the search bar already open, which is what
        // gives filter a fullscreen mode without a second screen implementation.
        _searchActive = request.StartInSearch;
        Rebuild();
    }

    public TuiScreenOutcome? Outcome { get; private set; }

    /// <summary>
    /// Rebuilds the layout, which changes when the search box opens or closes.
    /// </summary>
    private void Rebuild()
    {
        // The list sits in a titled box, which is what the separator line and the header
        // text were approximating before — a border says the same thing and frames the
        // scrolling region properly.
        _frame.Child = _list;

        var stack = new TuiStack(TuiOrientation.Vertical);

        if (_searchActive)
        {
            stack.Add(
                new TuiStack(TuiOrientation.Horizontal)
                    .Add(new TuiTextWidget("Search: "), TuiLength.Auto)
                    .Add(_search, TuiLength.Star()),
                TuiLength.Fixed(1));
        }

        stack.Add(_frame, TuiLength.Star())
             .Add(_footer, TuiLength.Fixed(1));

        _content = stack;

        // Focus is rebuilt with the tree: it walks from a root, and the root changes
        // when the search box opens or closes.
        _focus = new TuiFocus(_content);
        _focus.Focus(_searchActive ? _search : _list);
    }

    public TuiFrame Render(TuiSize size)
    {
        var buffer = new TuiBuffer(size);
        var bounds = new TuiRect(0, 0, size.Width, size.Height);

        _frame.Title = _request.MultiSelect
            ? $"{_request.Prompt ?? "Select items"} ({_ticked.Count} selected)"
            : _request.Prompt ?? "Select an item";

        _footer.Text = _request.MultiSelect
            ? "Up/Down move   Space toggle   Enter confirm   / search   Esc cancel"
            : "Up/Down move   Enter select   / search   Esc cancel";

        _content.Measure(TuiConstraints.From(size));
        _content.Arrange(bounds);
        _content.Paint(new TuiSurface(buffer, bounds));

        if (_searchActive)
        {
            // The terminal draws a better caret than a reversed cell does.
            buffer.Cursor = (_search.Bounds.Left + _search.CaretColumn, _search.Bounds.Top);
        }

        return new TuiFrame(buffer);
    }

    public TuiScreenResult HandleInput(TuiInputEvent input)
    {
        // In a chooser the wheel moves the choice rather than the view. That is what this
        // screen has always done, and it is the useful behaviour here: the point of the
        // screen is to end up on an item, not to read past it.
        if (!input.IsKey && input.Mouse.Action == TuiMouseAction.Scroll)
        {
            _list.SelectedIndex += input.Mouse.Button == TuiMouseButton.ScrollUp ? -1 : 1;
            return TuiScreenResult.Continue;
        }

        // The focused widget gets first refusal; the screen acts only on what it declined.
        if (_focus.Dispatch(input))
        {
            return Outcome is null ? TuiScreenResult.Continue : TuiScreenResult.Exit;
        }

        if (input.IsKey)
        {
            return HandleKey(input.Key);
        }

        return TuiScreenResult.Continue;
    }

    public TuiScreenResult HandleKey(ConsoleKeyInfo key)
    {
        // Reached only when nothing in the tree wanted the key — so a letter typed into
        // the search box never arrives here, and `q` is a shortcut only when it is not
        // being typed (`TOSH-0011`).
        switch (key.Key)
        {
            case ConsoleKey.Enter:
                Commit();
                return TuiScreenResult.Exit;

            case ConsoleKey.Escape:
            case ConsoleKey.Q:
                Outcome = new TuiScreenOutcome { Cancelled = true };
                return TuiScreenResult.Exit;

            case ConsoleKey.Tab:
                _focus.MoveNext();
                return TuiScreenResult.Continue;
        }

        if (key.KeyChar == '/' && !_searchActive)
        {
            _searchActive = true;
            Rebuild();
        }

        return TuiScreenResult.Continue;
    }

    private void CloseSearch(bool keepQuery)
    {
        _searchActive = false;

        if (!keepQuery)
        {
            _search.Text = string.Empty;
            ApplyFilter();
        }

        Rebuild();
    }

    private void ApplyFilter()
    {
        var query = _search.Text.Trim();

        _list.Items = query.Length == 0
            ? _request.Items
            : _request.Items.Where(item => FormatItem(item).Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    private void Toggle(object? item)
    {
        var index = OriginalIndexOf(item);

        if (index >= 0 && !_ticked.Remove(index))
        {
            _ticked.Add(index);
        }
    }

    private void Commit()
    {
        if (_request.MultiSelect)
        {
            Outcome = new TuiScreenOutcome
            {
                Selected = _ticked.OrderBy(index => index).Select(index => _request.Items[index]).ToArray(),
                Cancelled = false,
            };

            return;
        }

        Outcome = _list.SelectedItem is { } selected
            ? new TuiScreenOutcome { Selected = [selected], Cancelled = false }
            : new TuiScreenOutcome { Cancelled = true };
    }

    private int OriginalIndexOf(object? item)
    {
        for (var index = 0; index < _request.Items.Count; index += 1)
        {
            if (ReferenceEquals(_request.Items[index], item) || Equals(_request.Items[index], item))
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>
    /// How an item is labelled: an asked-for property, then the shell's own rendering,
    /// then a name-like property, then the value itself.
    /// </summary>
    private string FormatItem(object? item)
    {
        if (item is null)
        {
            return "(null)";
        }

        if (_request.DisplayProperty is not null)
        {
            return TuiList.FormatValue(item, _request.DisplayProperty);
        }

        var type = item.GetType();

        if (_formatter is not null)
        {
            var options = new ObjectFormattingOptions(ObjectRenderStyle.Compact);

            if (_formatter.TryRenderProfile(item, options, DisplaySurface.Root, out var text) &&
                !string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        if (!type.IsPrimitive && type != typeof(string) && !type.IsEnum)
        {
            foreach (var name in (string[])["Name", "DisplayName", "Title", "Label"])
            {
                var property = type.GetProperty(
                    name,
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);

                if (property?.GetValue(item)?.ToString() is { } label)
                {
                    return label;
                }
            }
        }

        return item.ToString() ?? string.Empty;
    }
}
