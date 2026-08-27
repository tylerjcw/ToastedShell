namespace Tosh.Runtime;

public sealed record InspectVisibleNode(
    InspectTreeNode Node,
    int Depth,
    int? ParentIndex);
