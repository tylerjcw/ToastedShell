namespace Tosh.Cli.Tui;

internal static class TuiSplitLayout
{
    public static (TuiRect First, TuiRect Second) SplitColumns(TuiRect bounds, int firstWidth, int gap = 1)
    {
        var normalizedGap = Math.Max(0, gap);
        var clampedFirstWidth = Math.Clamp(firstWidth, 0, Math.Max(0, bounds.Width - normalizedGap));
        var secondLeft = bounds.Left + clampedFirstWidth + normalizedGap;
        var secondWidth = Math.Max(0, bounds.Width - clampedFirstWidth - normalizedGap);

        return
        (
            new TuiRect(bounds.Left, bounds.Top, clampedFirstWidth, bounds.Height),
            new TuiRect(secondLeft, bounds.Top, secondWidth, bounds.Height)
        );
    }

    public static (TuiRect First, TuiRect Second) SplitRows(TuiRect bounds, int firstHeight, int gap = 1)
    {
        var normalizedGap = Math.Max(0, gap);
        var clampedFirstHeight = Math.Clamp(firstHeight, 0, Math.Max(0, bounds.Height - normalizedGap));
        var secondTop = bounds.Top + clampedFirstHeight + normalizedGap;
        var secondHeight = Math.Max(0, bounds.Height - clampedFirstHeight - normalizedGap);

        return
        (
            new TuiRect(bounds.Left, bounds.Top, bounds.Width, clampedFirstHeight),
            new TuiRect(bounds.Left, secondTop, bounds.Width, secondHeight)
        );
    }
}
