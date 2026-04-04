using System.Text;
using Tosh.Tui;
using Tosh.Tui.Requests;
using Tosh.Tui.Widgets;

namespace Tosh.Cli.Tui;

internal sealed class TuiCustomScreen : ITuiScreen
{
    private readonly TuiRunRequest _request;
    private readonly TuiScreen _definition;
    private readonly List<WidgetHost> _widgetHosts = new();
    private int _focusIndex;

    public TuiCustomScreen(TuiRunRequest request)
    {
        _request = request;
        _definition = request.Screen;
        InitializeWidgets();
    }

    public TuiScreenOutcome? Outcome { get; private set; }

    public TuiFrame Render(TuiSize size)
    {
        if (_widgetHosts.Count == 0)
        {
            return new TuiFrame("(empty screen)");
        }

        var sb = new StringBuilder();
        var width = size.Width;
        var height = size.Height;

        // Render title bar
        var titleHeight = 0;

        if (_definition.ScreenTitle is not null)
        {
            var title = _definition.ScreenTitle;
            sb.AppendLine(title.Length > width ? title[..width] : title);
            sb.AppendLine(new string('─', Math.Min(width, 80)));
            titleHeight = 2;
        }

        var contentHeight = height - titleHeight - 2; // Reserve footer
        var contentBounds = new TuiRect(0, titleHeight, width, Math.Max(1, contentHeight));

        // Compute layout rectangles
        var regions = ComputeLayout(contentBounds);

        // Build a set of rendered lines indexed by row
        var lines = new string[Math.Max(0, contentHeight)];

        for (var i = 0; i < lines.Length; i++)
        {
            lines[i] = new string(' ', width);
        }

        for (var wi = 0; wi < _widgetHosts.Count && wi < regions.Count; wi++)
        {
            var region = regions[wi];
            var host = _widgetHosts[wi];
            var isFocused = wi == _focusIndex;
            var widgetLines = host.Render(region.Width, region.Height, isFocused);

            for (var row = 0; row < widgetLines.Count && region.Top - titleHeight + row < lines.Length; row++)
            {
                var lineIndex = region.Top - titleHeight + row;

                if (lineIndex < 0)
                {
                    continue;
                }

                var rendered = widgetLines[row];

                if (rendered.Length > region.Width)
                {
                    rendered = rendered[..region.Width];
                }

                // Place into line at column offset
                var line = lines[lineIndex];
                var before = line[..Math.Min(region.Left, line.Length)];
                var after = region.Right < line.Length ? line[region.Right..] : string.Empty;
                var padded = rendered + new string(' ', Math.Max(0, region.Width - rendered.Length));
                lines[lineIndex] = before + padded + after;
            }
        }

        foreach (var line in lines)
        {
            sb.AppendLine(line);
        }

        // Footer
        sb.AppendLine();
        var help = "Tab: next widget | Enter: confirm | Esc: cancel";
        sb.Append(help.Length > width ? help[..width] : help);

        return new TuiFrame(sb.ToString());
    }

    public TuiScreenResult HandleKey(ConsoleKeyInfo key)
    {
        // Global keys
        switch (key.Key)
        {
            case ConsoleKey.Escape:
                Cancel();
                return TuiScreenResult.Exit;

            case ConsoleKey.Tab:
                if (_widgetHosts.Count > 1)
                {
                    _focusIndex = (_focusIndex + 1) % _widgetHosts.Count;
                }

                return TuiScreenResult.Continue;

            case ConsoleKey.Enter when (key.Modifiers & ConsoleModifiers.Control) != 0:
                Commit();
                return TuiScreenResult.Exit;
        }

        // Delegate to focused widget
        if (_widgetHosts.Count > 0 && _focusIndex < _widgetHosts.Count)
        {
            var result = _widgetHosts[_focusIndex].HandleKey(key);

            if (result == WidgetKeyResult.Exit)
            {
                Commit();
                return TuiScreenResult.Exit;
            }

            // Resolve bindings after state change
            ResolveBindings();
        }

        return TuiScreenResult.Continue;
    }

    private void InitializeWidgets()
    {
        foreach (var widget in _definition.Widgets)
        {
            var host = CreateWidgetHost(widget);

            if (host is not null)
            {
                _widgetHosts.Add(host);
            }
        }
    }

    private static WidgetHost? CreateWidgetHost(ITuiWidget widget)
    {
        return widget switch
        {
            TuiListWidgetConfig list => new ListWidgetHost(list),
            TuiTextWidgetConfig text => new TextWidgetHost(text),
            TuiTextInputConfig input => new TextInputWidgetHost(input),
            TuiOptionPickerConfig picker => new OptionPickerWidgetHost(picker),
            TuiConfirmationConfig confirm => new ConfirmationWidgetHost(confirm),
            TuiFilePickerConfig file => new FilePickerWidgetHost(file),
            _ => null,
        };
    }

    private IReadOnlyList<TuiRect> ComputeLayout(TuiRect bounds)
    {
        var count = _widgetHosts.Count;

        if (count == 0)
        {
            return Array.Empty<TuiRect>();
        }

        var layout = _definition.LayoutConfig;

        if (count == 1 || layout.Layout == Tosh.Tui.TuiLayout.Single)
        {
            return [bounds];
        }

        if (layout.Layout == Tosh.Tui.TuiLayout.SplitHorizontal && count >= 2)
        {
            var (first, second) = layout.ParseRatio();
            var total = first + second;
            var firstWidth = bounds.Width * first / total;
            var (left, right) = TuiSplitLayout.SplitColumns(bounds, firstWidth, layout.Gap);

            if (count == 2)
            {
                return [left, right];
            }

            // Extra widgets stack in the right pane
            return StackInRegion(left, right, count);
        }

        if (layout.Layout == Tosh.Tui.TuiLayout.SplitVertical && count >= 2)
        {
            var (first, second) = layout.ParseRatio();
            var total = first + second;
            var firstHeight = bounds.Height * first / total;
            var (top, bottom) = TuiSplitLayout.SplitRows(bounds, firstHeight, layout.Gap);

            if (count == 2)
            {
                return [top, bottom];
            }

            return StackInRegion(top, bottom, count);
        }

        // Stacked: divide height equally
        return StackEvenly(bounds, count, layout.Gap);
    }

    private static IReadOnlyList<TuiRect> StackInRegion(TuiRect first, TuiRect second, int total)
    {
        var regions = new List<TuiRect> { first };
        var remaining = total - 1;
        var heightPer = remaining > 0 ? second.Height / remaining : second.Height;

        for (var i = 0; i < remaining; i++)
        {
            regions.Add(new TuiRect(second.Left, second.Top + i * heightPer, second.Width, heightPer));
        }

        return regions;
    }

    private static IReadOnlyList<TuiRect> StackEvenly(TuiRect bounds, int count, int gap)
    {
        var totalGap = gap * (count - 1);
        var available = Math.Max(count, bounds.Height - totalGap);
        var heightPer = available / count;
        var regions = new List<TuiRect>();

        for (var i = 0; i < count; i++)
        {
            regions.Add(new TuiRect(bounds.Left, bounds.Top + i * (heightPer + gap), bounds.Width, heightPer));
        }

        return regions;
    }

    private void ResolveBindings()
    {
        foreach (var host in _widgetHosts)
        {
            if (host is TextWidgetHost textHost && textHost.Config.Binding is TuiWidgetBinding binding)
            {
                var sourceHost = _widgetHosts.FirstOrDefault(h => string.Equals(h.WidgetId, binding.SourceWidgetId, StringComparison.OrdinalIgnoreCase));

                if (sourceHost is not null)
                {
                    var value = sourceHost.GetBoundValue(binding.Property);
                    textHost.SetBoundContent(value);
                }
            }
        }
    }

    private void Commit()
    {
        var values = new Dictionary<string, object?>();
        var selected = new List<object?>();

        foreach (var host in _widgetHosts)
        {
            var (key, value) = host.GetValue();
            values[key] = value;

            if (host is ListWidgetHost listHost)
            {
                if (listHost.TryGetSelectedItem(out var item))
                {
                    selected.Add(item);
                }
            }
        }

        Outcome = new TuiScreenOutcome
        {
            Selected = selected,
            Cancelled = false,
            Values = values,
        };
    }

    private void Cancel()
    {
        Outcome = new TuiScreenOutcome { Cancelled = true };
    }

    // ── Widget Hosts ──────────────────────────────────────────

    private enum WidgetKeyResult
    {
        Continue,
        Exit,
    }

    private abstract class WidgetHost
    {
        public abstract string WidgetId { get; }
        public abstract IReadOnlyList<string> Render(int width, int height, bool focused);
        public abstract WidgetKeyResult HandleKey(ConsoleKeyInfo key);
        public abstract (string Key, object? Value) GetValue();
        public abstract object? GetBoundValue(string property);
    }

    private sealed class ListWidgetHost : WidgetHost
    {
        private readonly TuiListWidgetConfig _config;
        private readonly TuiListState<object?> _list = new();

        public ListWidgetHost(TuiListWidgetConfig config)
        {
            _config = config;
        }

        public TuiListWidgetConfig Config => _config;

        public override string WidgetId => _config.Id;

        public bool TryGetSelectedItem(out object? item) => _list.TryGetSelected(out item);

        public override IReadOnlyList<string> Render(int width, int height, bool focused)
        {
            var lines = new List<string>();
            var pageSize = Math.Max(1, height - 1);
            _list.SetItems(_config.Items, pageSize);

            if (_config.Prompt is not null)
            {
                lines.Add(_config.Prompt);
                pageSize = Math.Max(1, height - 2);
                _list.SetItems(_config.Items, pageSize);
            }

            var range = _list.Scroll.GetVisibleRange();

            for (var row = 0; row < range.Length; row++)
            {
                var itemIndex = range.Start + row;
                var item = _config.Items[itemIndex];
                var isHighlighted = itemIndex == _list.SelectedIndex;
                var prefix = isHighlighted && focused ? "> " : "  ";
                lines.Add(prefix + FormatItem(item));
            }

            return lines;
        }

        public override WidgetKeyResult HandleKey(ConsoleKeyInfo key)
        {
            switch (key.Key)
            {
                case ConsoleKey.UpArrow: _list.MovePrevious(); break;
                case ConsoleKey.DownArrow: _list.MoveNext(); break;
                case ConsoleKey.PageUp: _list.PageUp(); break;
                case ConsoleKey.PageDown: _list.PageDown(); break;
                case ConsoleKey.Home: _list.Home(); break;
                case ConsoleKey.End: _list.End(); break;
                case ConsoleKey.Enter: return WidgetKeyResult.Exit;
            }

            return WidgetKeyResult.Continue;
        }

        public override (string Key, object? Value) GetValue()
        {
            _list.TryGetSelected(out var item);
            return (_config.Id, item);
        }

        public override object? GetBoundValue(string property)
        {
            if (string.Equals(property, "selected", StringComparison.OrdinalIgnoreCase))
            {
                _list.TryGetSelected(out var item);
                return item;
            }

            return null;
        }

        private string FormatItem(object? item)
        {
            if (item is null)
            {
                return "(null)";
            }

            if (_config.DisplayProperty is not null)
            {
                var prop = item.GetType().GetProperty(_config.DisplayProperty,
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase);

                if (prop is not null)
                {
                    return prop.GetValue(item)?.ToString() ?? string.Empty;
                }
            }

            return item.ToString() ?? string.Empty;
        }
    }

    private sealed class TextWidgetHost : WidgetHost
    {
        private readonly TuiTextWidgetConfig _config;
        private readonly TuiScrollState _scroll = new();
        private object? _boundContent;

        public TextWidgetHost(TuiTextWidgetConfig config)
        {
            _config = config;
        }

        public TuiTextWidgetConfig Config => _config;

        public override string WidgetId => _config.Id;

        public void SetBoundContent(object? content)
        {
            _boundContent = content;
        }

        public override IReadOnlyList<string> Render(int width, int height, bool focused)
        {
            var content = _config.Binding is not null ? _boundContent : _config.Content;
            var text = content?.ToString() ?? string.Empty;
            var allLines = _config.WordWrap
                ? TextDocumentFormatter.WrapParagraph(text, width)
                : text.Split('\n');

            _scroll.SetDimensions(allLines.Count, Math.Max(1, height));
            var range = _scroll.GetVisibleRange();
            var lines = new List<string>();

            for (var i = range.Start; i < range.Start + range.Length && i < allLines.Count; i++)
            {
                lines.Add(allLines[i]);
            }

            return lines;
        }

        public override WidgetKeyResult HandleKey(ConsoleKeyInfo key)
        {
            switch (key.Key)
            {
                case ConsoleKey.UpArrow: _scroll.LineUp(); break;
                case ConsoleKey.DownArrow: _scroll.LineDown(); break;
                case ConsoleKey.PageUp: _scroll.PageUp(); break;
                case ConsoleKey.PageDown: _scroll.PageDown(); break;
            }

            return WidgetKeyResult.Continue;
        }

        public override (string Key, object? Value) GetValue()
        {
            var content = _config.Binding is not null ? _boundContent : _config.Content;
            return (_config.Id, content);
        }

        public override object? GetBoundValue(string property)
        {
            return null;
        }
    }

    private sealed class TextInputWidgetHost : WidgetHost
    {
        private readonly TuiTextInputConfig _config;
        private readonly TuiTextInputState _input = new();

        public TextInputWidgetHost(TuiTextInputConfig config)
        {
            _config = config;
            _input.SetText(config.DefaultValue);
        }

        public override string WidgetId => _config.Id;

        public override IReadOnlyList<string> Render(int width, int height, bool focused)
        {
            var lines = new List<string>();

            if (_config.Prompt is not null)
            {
                lines.Add(_config.Prompt);
            }

            lines.Add(focused ? _input.RenderWithCursor() : _input.Text);
            return lines;
        }

        public override WidgetKeyResult HandleKey(ConsoleKeyInfo key)
        {
            _input.HandleKey(key);
            return WidgetKeyResult.Continue;
        }

        public override (string Key, object? Value) GetValue()
        {
            return (_config.Id, _input.Text);
        }

        public override object? GetBoundValue(string property)
        {
            if (string.Equals(property, "text", StringComparison.OrdinalIgnoreCase))
            {
                return _input.Text;
            }

            return null;
        }
    }

    private sealed class OptionPickerWidgetHost : WidgetHost
    {
        private readonly TuiOptionPickerConfig _config;
        private readonly TuiOptionPickerState<object?> _picker = new();

        public OptionPickerWidgetHost(TuiOptionPickerConfig config)
        {
            _config = config;
            _picker.Open(config.Options, 20, item => FormatItem(item), preferredKey: null);
        }

        public override string WidgetId => _config.Id;

        public override IReadOnlyList<string> Render(int width, int height, bool focused)
        {
            var lines = new List<string>();

            if (_config.Prompt is not null)
            {
                lines.Add(_config.Prompt);
            }

            var pageSize = Math.Max(1, height - (lines.Count));
            _picker.Refresh(_config.Options, pageSize);

            for (var i = 0; i < _config.Options.Count && i < pageSize; i++)
            {
                var isHighlighted = i == _picker.SelectedIndex;
                var prefix = isHighlighted && focused ? "> " : "  ";
                lines.Add(prefix + FormatItem(_config.Options[i]));
            }

            return lines;
        }

        public override WidgetKeyResult HandleKey(ConsoleKeyInfo key)
        {
            var action = _picker.HandleKey(key);

            if (action.Kind == TuiOptionPickerActionKind.Commit)
            {
                return WidgetKeyResult.Exit;
            }

            return WidgetKeyResult.Continue;
        }

        public override (string Key, object? Value) GetValue()
        {
            _picker.TryGetSelected(out var item);
            return (_config.Id, item);
        }

        public override object? GetBoundValue(string property)
        {
            if (string.Equals(property, "selected", StringComparison.OrdinalIgnoreCase))
            {
                _picker.TryGetSelected(out var item);
                return item;
            }

            return null;
        }

        private string FormatItem(object? item)
        {
            if (item is null)
            {
                return "(null)";
            }

            if (_config.DisplayProperty is not null)
            {
                var prop = item.GetType().GetProperty(_config.DisplayProperty,
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase);

                if (prop is not null)
                {
                    return prop.GetValue(item)?.ToString() ?? string.Empty;
                }
            }

            return item.ToString() ?? string.Empty;
        }
    }

    private sealed class ConfirmationWidgetHost : WidgetHost
    {
        private readonly TuiConfirmationConfig _config;
        private readonly TuiConfirmationDialogState _dialog = new();

        public ConfirmationWidgetHost(TuiConfirmationConfig config)
        {
            _config = config;
            _dialog.Open(
                title: "Confirm",
                message: config.Message,
                confirmLabel: config.ConfirmLabel,
                cancelLabel: config.CancelLabel,
                confirmSelected: config.DefaultConfirm);
        }

        public override string WidgetId => _config.Id;

        public override IReadOnlyList<string> Render(int width, int height, bool focused)
        {
            return _dialog.BuildEntries(width);
        }

        public override WidgetKeyResult HandleKey(ConsoleKeyInfo key)
        {
            var result = _dialog.HandleKey(key);

            return result.Kind switch
            {
                TuiConfirmationDialogResultKind.Confirmed => WidgetKeyResult.Exit,
                TuiConfirmationDialogResultKind.Cancelled => WidgetKeyResult.Exit,
                _ => WidgetKeyResult.Continue,
            };
        }

        public override (string Key, object? Value) GetValue()
        {
            return (_config.Id, _dialog.ConfirmSelected);
        }

        public override object? GetBoundValue(string property)
        {
            return null;
        }
    }

    private sealed class FilePickerWidgetHost : WidgetHost
    {
        private readonly TuiFilePickerConfig _config;
        private readonly TuiFilePickerState _picker = new();

        public FilePickerWidgetHost(TuiFilePickerConfig config)
        {
            _config = config;

            var selectionMode = config.DirectoryOnly
                ? TuiFilePickerSelectionMode.Directory
                : TuiFilePickerSelectionMode.Any;
            _picker.Open(config.InitialPath ?? Environment.CurrentDirectory, selectionMode, initialSelectionPath: null, pageSize: 20);
        }

        public override string WidgetId => _config.Id;

        public override IReadOnlyList<string> Render(int width, int height, bool focused)
        {
            return _picker.BuildEntries(width, height);
        }

        public override WidgetKeyResult HandleKey(ConsoleKeyInfo key)
        {
            var result = _picker.HandleKey(key, 20);

            return result.Kind switch
            {
                TuiFilePickerResultKind.Selected => WidgetKeyResult.Exit,
                TuiFilePickerResultKind.Cancelled => WidgetKeyResult.Exit,
                _ => WidgetKeyResult.Continue,
            };
        }

        public override (string Key, object? Value) GetValue()
        {
            return (_config.Id, _picker.CurrentDirectory);
        }

        public override object? GetBoundValue(string property)
        {
            return null;
        }
    }
}
