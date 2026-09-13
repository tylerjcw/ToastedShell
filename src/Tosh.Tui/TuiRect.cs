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

    /// <summary>Tests whether the given column/row falls within this rectangle.</summary>
    public bool Contains(int column, int row) =>
        column >= Left && column < Right && row >= Top && row < Bottom;

    /// <summary>The overlap with another rectangle, empty when they do not meet.</summary>
    public TuiRect Intersect(TuiRect other)
    {
        var left = Math.Max(Left, other.Left);
        var top = Math.Max(Top, other.Top);
        var right = Math.Min(Right, other.Right);
        var bottom = Math.Min(Bottom, other.Bottom);

        return right <= left || bottom <= top
            ? new TuiRect(left, top, 0, 0)
            : new TuiRect(left, top, right - left, bottom - top);
    }

    /// <summary>This rectangle moved by an offset.</summary>
    public TuiRect Offset(int columns, int rows) =>
        new(Left + columns, Top + rows, Width, Height);
}
