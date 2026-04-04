namespace Tosh.Tui;

/// <summary>A rectangular region within the terminal area.</summary>
public readonly record struct TuiRect
{
    public TuiRect(int left, int top, int width, int height)
    {
        Left = Math.Max(0, left);
        Top = Math.Max(0, top);
        Width = Math.Max(0, width);
        Height = Math.Max(0, height);
    }

    public int Left { get; }

    public int Top { get; }

    public int Width { get; }

    public int Height { get; }

    public int Right => Left + Width;

    public int Bottom => Top + Height;

    public bool IsEmpty => Width == 0 || Height == 0;
}
