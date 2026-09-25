namespace Tosh.Stdlib.Plotting;

/// <summary>
/// Margins surrounding a plotting region in display units.
/// </summary>
public readonly record struct PlotMargins(float Left, float Right, float Top, float Bottom)
{
    public static readonly PlotMargins Default = new(65f, 25f, 40f, 50f);
    public static readonly PlotMargins Compact = new(45f, 15f, 25f, 35f);
    public static readonly PlotMargins Zero = new(0f, 0f, 0f, 0f);
}
