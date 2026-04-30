namespace Tosh.Runtime;

public sealed record class ColumnSummary
{
    public string Column { get; init; } = "Value";

    public int RowCount { get; init; }

    public int ValueCount { get; init; }

    public long? Count { get; init; }

    public object? Sum { get; init; }

    public object? Average { get; init; }

    public object? Min { get; init; }

    public object? Max { get; init; }

    public override string ToString()
    {
        return Column;
    }
}
