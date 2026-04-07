namespace Tosh.Core;

public sealed record InspectVisibleNode(
    InspectTreeNode Node,
    int Depth,
    int? ParentIndex);
