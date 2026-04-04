namespace Tosh.Tui.Widgets;

/// <summary>Configuration for a selectable list widget.</summary>
public sealed class TuiListWidgetConfig : ITuiWidget
{
    public TuiListWidgetConfig(string id, IReadOnlyList<object?> items)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(items);

        Id = id;
        Items = items;
    }

    public string Id { get; }

    public TuiWidgetKind Kind => TuiWidgetKind.List;

    /// <summary>Items to display in the list.</summary>
    public IReadOnlyList<object?> Items { get; }

    /// <summary>Property name used to produce the display label for each item.
    /// When null, <c>ToString()</c> is used.</summary>
    public string? DisplayProperty { get; set; }

    /// <summary>Allow multiple selections.</summary>
    public bool MultiSelect { get; set; }

    /// <summary>Show a search/filter bar above the list.</summary>
    public bool Searchable { get; set; }

    /// <summary>Prompt text shown at the top of the list.</summary>
    public string? Prompt { get; set; }
}
