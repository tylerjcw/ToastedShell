namespace Tosh.Tui;

/// <summary>Terminal area dimensions in columns and rows.</summary>
public readonly record struct TuiSize
{
    public TuiSize(int width, int height)
    {
        Width = Math.Max(0, width);
        Height = Math.Max(0, height);
    }

    public int Width { get; }

    public int Height { get; }
}
