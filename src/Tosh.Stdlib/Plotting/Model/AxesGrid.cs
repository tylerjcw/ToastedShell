namespace Tosh.Stdlib.Plotting;

public sealed class AxesGrid
{
    private readonly Axes[,] _grid;
    public int Rows { get; }
    public int Columns { get; }

    public AxesGrid(int rows, int columns)
    {
        Rows = Math.Max(1, rows);
        Columns = Math.Max(1, columns);
        _grid = new Axes[Rows, Columns];
        for (int r = 0; r < Rows; r++)
        {
            for (int c = 0; c < Columns; c++)
            {
                _grid[r, c] = new Axes();
            }
        }
    }

    public Axes At(int row, int column)
    {
        if (row < 0 || row >= Rows || column < 0 || column >= Columns)
        {
            throw new ArgumentOutOfRangeException($"Grid coordinates ({row}, {column}) out of bounds ({Rows}, {Columns}).");
        }
        return _grid[row, column];
    }
}
