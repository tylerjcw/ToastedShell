namespace Tosh.Core;

public sealed record ObjectFormattingOptions(
    ObjectRenderStyle Style,
    int MaxDepth = 3,
    int MaxCollectionItemCount = 8,
    int MaxPropertyCount = 8,
    int IndentSize = 2);
