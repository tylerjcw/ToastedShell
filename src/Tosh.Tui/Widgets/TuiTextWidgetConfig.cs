namespace Tosh.Tui.Widgets;

/// <summary>Configuration for a read-only text or object detail widget.</summary>
public sealed class TuiTextWidgetConfig : ITuiWidget
{
    public TuiTextWidgetConfig(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        Id = id;
    }

    public string Id { get; }

    public TuiWidgetKind Kind => TuiWidgetKind.Text;

    /// <summary>Static content to display. Ignored when <see cref="Binding"/> is set.</summary>
    public object? Content { get; set; }

    /// <summary>When set, the widget shows the bound source widget's selected value instead of static content.</summary>
    public TuiWidgetBinding? Binding { get; set; }

    /// <summary>Wrap long lines to the widget width.</summary>
    public bool WordWrap { get; set; } = true;
}
