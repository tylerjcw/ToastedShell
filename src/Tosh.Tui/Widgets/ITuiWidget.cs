namespace Tosh.Tui.Widgets;

/// <summary>A widget definition within a <see cref="TuiScreen"/>.</summary>
public interface ITuiWidget
{
    /// <summary>Unique identifier used for layout placement and widget bindings.</summary>
    string Id { get; }

    /// <summary>The kind of widget this represents.</summary>
    TuiWidgetKind Kind { get; }
}
