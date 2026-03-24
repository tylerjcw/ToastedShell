namespace Tosh.Core;

public sealed record DisplayRenderOptions(
    ObjectRenderStyle Style,
    int? MaxWidth = null,
    int MaxTableCellWidth = 36,
    int TableColumnSpacing = 2);
