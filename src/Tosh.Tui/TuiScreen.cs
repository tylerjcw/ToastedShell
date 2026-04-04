using Tosh.Tui.Widgets;

namespace Tosh.Tui;

/// <summary>
/// A script-constructed TUI screen definition. Supports fluent builder methods and
/// config-object construction. Pass to <c>tui run</c> to launch.
/// </summary>
public sealed class TuiScreen
{
    private readonly List<ITuiWidget> _widgets = new();
    private TuiLayoutConfig _layout = new();

    /// <summary>Creates an empty screen definition.</summary>
    public TuiScreen() { }

    /// <summary>Creates a screen from a config dictionary (ExpandoObject / anonymous type).
    /// Recognized keys: Title, Layout, Ratio, Gap, Widgets.</summary>
    public TuiScreen(IDictionary<string, object?> config)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (config.TryGetValue("Title", out var title) && title is string titleStr)
        {
            ScreenTitle = titleStr;
        }

        if (config.TryGetValue("Layout", out var layout))
        {
            if (layout is string layoutStr && Enum.TryParse<TuiLayout>(layoutStr, ignoreCase: true, out var parsed))
            {
                _layout.Layout = parsed;
            }
            else if (layout is TuiLayout tuiLayout)
            {
                _layout.Layout = tuiLayout;
            }
        }

        if (config.TryGetValue("Ratio", out var ratio) && ratio is string ratioStr)
        {
            _layout.Ratio = ratioStr;
        }

        if (config.TryGetValue("Gap", out var gap) && gap is int gapInt)
        {
            _layout.Gap = gapInt;
        }
    }

    /// <summary>Screen title displayed in the header bar.</summary>
    public string? ScreenTitle { get; set; }

    /// <summary>Layout configuration.</summary>
    public TuiLayoutConfig LayoutConfig => _layout;

    /// <summary>Registered widgets in insertion order.</summary>
    public IReadOnlyList<ITuiWidget> Widgets => _widgets;

    /// <summary>Sets the screen title. Returns this screen for chaining.</summary>
    public TuiScreen Title(string title)
    {
        ScreenTitle = title;
        return this;
    }

    /// <summary>Sets the layout orientation. Returns this screen for chaining.</summary>
    public TuiScreen SetLayout(TuiLayout layout)
    {
        _layout.Layout = layout;
        return this;
    }

    /// <summary>Sets the layout ratio. Returns this screen for chaining.</summary>
    public TuiScreen SetRatio(string ratio)
    {
        _layout.Ratio = ratio;
        return this;
    }

    /// <summary>Sets the gap between panes. Returns this screen for chaining.</summary>
    public TuiScreen SetGap(int gap)
    {
        _layout.Gap = gap;
        return this;
    }

    /// <summary>Adds a widget to this screen. Returns this screen for chaining.</summary>
    public TuiScreen AddWidget(ITuiWidget widget)
    {
        ArgumentNullException.ThrowIfNull(widget);
        _widgets.Add(widget);
        return this;
    }

    /// <summary>Retrieves a widget by its id.</summary>
    public ITuiWidget? GetWidget(string id)
    {
        return _widgets.FirstOrDefault(w => string.Equals(w.Id, id, StringComparison.OrdinalIgnoreCase));
    }
}
