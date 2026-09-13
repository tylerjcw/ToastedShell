namespace Tosh.Tui.Widgets;

/// <summary>What the widgets in a tree are currently holding, keyed by their ids.</summary>
/// <remarks>
/// An id is the only way to name a widget that nothing holds a reference to, which is the
/// situation a tree written as markup is always in. A widget without one is skipped rather
/// than given a synthetic name: an unnamed widget is one nobody asked about.
/// </remarks>
public static class TuiValues
{
    /// <summary>Collects the value of every identified widget at or under <paramref name="root"/>.</summary>
    public static IDictionary<string, object?> Collect(TuiWidget root)
    {
        ArgumentNullException.ThrowIfNull(root);

        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        Walk(root);

        return values;

        void Walk(TuiWidget widget)
        {
            if (widget.Id is { Length: > 0 } id)
            {
                values[id] = widget.Value;
            }

            foreach (var child in widget.Children)
            {
                Walk(child);
            }
        }
    }
}
