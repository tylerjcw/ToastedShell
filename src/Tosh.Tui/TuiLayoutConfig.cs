namespace Tosh.Tui;

/// <summary>Describes how widgets are arranged within a <see cref="TuiScreen"/>.</summary>
public sealed class TuiLayoutConfig
{
    /// <summary>Layout orientation.</summary>
    public TuiLayout Layout { get; set; } = TuiLayout.Single;

    /// <summary>
    /// Ratio expressed as "first:second" (e.g. "30:70") controlling space allocation
    /// between the two panes in <see cref="TuiLayout.SplitHorizontal"/> or
    /// <see cref="TuiLayout.SplitVertical"/> layouts. Ignored for Single/Stacked.
    /// </summary>
    public string? Ratio { get; set; }

    /// <summary>Gap in columns (horizontal) or rows (vertical) between panes.</summary>
    public int Gap { get; set; } = 1;

    /// <summary>Parses a ratio string like "30:70" into two integer parts.</summary>
    public (int First, int Second) ParseRatio()
    {
        if (string.IsNullOrWhiteSpace(Ratio))
        {
            return (50, 50);
        }

        var parts = Ratio.Split(':');

        if (parts.Length == 2
            && int.TryParse(parts[0], out var first)
            && int.TryParse(parts[1], out var second)
            && first > 0
            && second > 0)
        {
            return (first, second);
        }

        return (50, 50);
    }
}
