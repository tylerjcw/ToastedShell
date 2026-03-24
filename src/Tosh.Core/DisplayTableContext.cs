namespace Tosh.Core;

public sealed record DisplayTableContext(
    Type RowType,
    IReadOnlyList<object> Rows,
    DisplayRenderOptions Options);
