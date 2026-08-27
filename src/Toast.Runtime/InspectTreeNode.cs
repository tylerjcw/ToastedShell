namespace Tosh.Runtime;

public sealed class InspectTreeNode
{
    private readonly Func<IReadOnlyList<InspectTreeNode>>? _childrenFactory;
    private IReadOnlyList<InspectTreeNode>? _children;

    public InspectTreeNode(
        InspectTreeNodeKind kind,
        string text,
        string? typeName = null,
        string? valuePreview = null,
        int? count = null,
        object? inspectValue = null,
        string? breadcrumbLabel = null,
        string? insertionSegment = null,
        bool isExpanded = false,
        Func<IReadOnlyList<InspectTreeNode>>? childrenFactory = null)
    {
        Kind = kind;
        Text = text;
        TypeName = typeName;
        ValuePreview = valuePreview;
        Count = count;
        InspectValue = inspectValue;
        BreadcrumbLabel = breadcrumbLabel;
        InsertionSegment = insertionSegment;
        IsExpanded = isExpanded;
        _childrenFactory = childrenFactory;
    }

    public InspectTreeNodeKind Kind { get; }

    public string Text { get; }

    public string? TypeName { get; }

    public string? ValuePreview { get; }

    public int? Count { get; }

    public object? InspectValue { get; }

    public string? BreadcrumbLabel { get; }

    public string? InsertionSegment { get; }

    public bool IsExpanded { get; set; }

    public bool HasChildren => _childrenFactory is not null || (_children?.Count ?? 0) > 0;

    public IReadOnlyList<InspectTreeNode> GetChildren()
    {
        _children ??= _childrenFactory?.Invoke() ?? Array.Empty<InspectTreeNode>();
        return _children;
    }
}
