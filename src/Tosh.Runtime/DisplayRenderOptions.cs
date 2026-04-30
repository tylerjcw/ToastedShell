namespace Tosh.Runtime;

public sealed record DisplayRenderOptions(
    ObjectRenderStyle Style,
    int? MaxWidth = null,
    int? MaxHeight = null,
    int MaxTableCellWidth = 36,
    int TableColumnSpacing = 2,
    Func<object?, DisplayColumnSelection?>? ColumnSelectionResolver = null,
    int MatrixLabelDepth = 0,
    bool PreferTensorSlices = false);
