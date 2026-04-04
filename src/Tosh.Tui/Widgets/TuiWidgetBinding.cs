namespace Tosh.Tui.Widgets;

/// <summary>Describes a binding from one widget to another widget's state.</summary>
public sealed record TuiWidgetBinding(string SourceWidgetId, string Property);
