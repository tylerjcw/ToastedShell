namespace Tosh.Runtime;

public sealed record DisplayTableContext(
    Type RowType,
    IReadOnlyList<object> Rows,
    DisplayRenderOptions Options);
