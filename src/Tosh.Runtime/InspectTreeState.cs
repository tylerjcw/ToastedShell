namespace Tosh.Runtime;

public sealed class InspectTreeState
{
    private readonly ObjectTreeBuilder _builder;
    private readonly bool _includeAllMembers;
    private readonly Stack<InspectTreeFrame> _frames = new();
    private IReadOnlyList<InspectVisibleNode> _visibleNodes = Array.Empty<InspectVisibleNode>();
    private string _filter = string.Empty;

    public InspectTreeState(ObjectTreeBuilder builder, object? rootValue, bool includeAllMembers = false, string? rootExpression = null)
    {
        _builder = builder ?? throw new ArgumentNullException(nameof(builder));
        _includeAllMembers = includeAllMembers;
        _frames.Push(builder.BuildFrame(rootValue, includeAllMembers, rootExpression: rootExpression ?? InspectInsertionUtilities.TryBuildRootExpression(rootValue)));
        Refresh();
    }

    public InspectTreeFrame CurrentFrame => _frames.Peek();

    public IReadOnlyList<InspectVisibleNode> VisibleNodes => _visibleNodes;

    public int SelectedIndex { get; private set; }

    public string Filter => _filter;

    public bool CanNavigateBack => _frames.Count > 1;

    public InspectVisibleNode? SelectedNode =>
        SelectedIndex >= 0 && SelectedIndex < _visibleNodes.Count ? _visibleNodes[SelectedIndex] : null;

    public void SetFilter(string value)
    {
        _filter = value ?? string.Empty;
        Refresh();
    }

    public void SelectIndex(int index)
    {
        if (_visibleNodes.Count == 0)
        {
            return;
        }

        SelectedIndex = Math.Clamp(index, 0, _visibleNodes.Count - 1);
    }

    public void MoveUp()
    {
        if (_visibleNodes.Count == 0)
        {
            return;
        }

        SelectedIndex = Math.Max(0, SelectedIndex - 1);
    }

    public void MoveDown()
    {
        if (_visibleNodes.Count == 0)
        {
            return;
        }

        SelectedIndex = Math.Min(_visibleNodes.Count - 1, SelectedIndex + 1);
    }

    public void MovePageUp(int amount)
    {
        if (_visibleNodes.Count == 0)
        {
            return;
        }

        SelectedIndex = Math.Max(0, SelectedIndex - Math.Max(1, amount));
    }

    public void MovePageDown(int amount)
    {
        if (_visibleNodes.Count == 0)
        {
            return;
        }

        SelectedIndex = Math.Min(_visibleNodes.Count - 1, SelectedIndex + Math.Max(1, amount));
    }

    public void MoveHome()
    {
        SelectedIndex = 0;
    }

    public void MoveEnd()
    {
        SelectedIndex = Math.Max(0, _visibleNodes.Count - 1);
    }

    public bool ExpandSelected()
    {
        var selected = SelectedNode?.Node;

        if (selected is null || !selected.HasChildren || selected.IsExpanded)
        {
            return false;
        }

        selected.IsExpanded = true;
        Refresh();
        return true;
    }

    public bool CollapseSelected()
    {
        var selected = SelectedNode;

        if (selected is null)
        {
            return false;
        }

        if (selected.Node.HasChildren && selected.Node.IsExpanded)
        {
            selected.Node.IsExpanded = false;
            Refresh();
            return true;
        }

        if (selected.ParentIndex is int parentIndex)
        {
            SelectedIndex = parentIndex;
            return true;
        }

        if (_frames.Count > 1)
        {
            _frames.Pop();
            Refresh();
            return true;
        }

        return false;
    }

    public bool DrillIntoSelected()
    {
        var selected = SelectedNode?.Node;

        if (selected?.InspectValue is null || string.IsNullOrWhiteSpace(selected.BreadcrumbLabel))
        {
            return false;
        }

        var breadcrumb = CurrentFrame.Breadcrumb.Concat([selected.BreadcrumbLabel!]).ToArray();
        _frames.Push(_builder.BuildFrame(
            selected.InspectValue,
            _includeAllMembers,
            breadcrumb,
            GetSelectedInsertionText()));
        Refresh();
        return true;
    }

    public string? GetSelectedInsertionText()
    {
        var selected = SelectedNode;

        if (selected is null)
        {
            return null;
        }

        var segments = new Stack<string>();
        var current = selected;

        while (true)
        {
            if (!string.IsNullOrWhiteSpace(current.Node.InsertionSegment))
            {
                segments.Push(current.Node.InsertionSegment!);
            }

            if (current.ParentIndex is not int parentIndex)
            {
                break;
            }

            current = _visibleNodes[parentIndex];
        }

        if (segments.Count == 0)
        {
            return null;
        }

        var path = string.Concat(segments);

        if (!string.IsNullOrWhiteSpace(CurrentFrame.RootExpression))
        {
            return InspectInsertionUtilities.ComposeInsertionText(CurrentFrame.RootExpression, path);
        }

        return path.StartsWith(".", StringComparison.Ordinal) ? path[1..] : path;
    }

    public void Refresh()
    {
        var visible = new List<InspectVisibleNode>();

        for (var index = 0; index < CurrentFrame.Nodes.Count; index++)
        {
            FlattenNode(CurrentFrame.Nodes[index], depth: 0, parentIndex: null, visible);
        }

        _visibleNodes = visible;
        SelectedIndex = Math.Clamp(SelectedIndex, 0, Math.Max(0, _visibleNodes.Count - 1));
    }

    private bool FlattenNode(InspectTreeNode node, int depth, int? parentIndex, List<InspectVisibleNode> visible)
    {
        var includeNode = string.IsNullOrWhiteSpace(_filter) || NodeOrDescendantMatches(node);

        if (!includeNode)
        {
            return false;
        }

        var currentIndex = visible.Count;
        visible.Add(new InspectVisibleNode(node, depth, parentIndex));

        if (string.IsNullOrWhiteSpace(_filter))
        {
            if (!node.IsExpanded)
            {
                return true;
            }

            foreach (var child in node.GetChildren())
            {
                FlattenNode(child, depth + 1, currentIndex, visible);
            }

            return true;
        }

        foreach (var child in node.GetChildren())
        {
            FlattenNode(child, depth + 1, currentIndex, visible);
        }

        return true;
    }

    private bool NodeOrDescendantMatches(InspectTreeNode node)
    {
        if (NodeMatches(node))
        {
            return true;
        }

        foreach (var child in node.GetChildren())
        {
            if (NodeOrDescendantMatches(child))
            {
                return true;
            }
        }

        return false;
    }

    private bool NodeMatches(InspectTreeNode node)
    {
        return node.Text.Contains(_filter, StringComparison.OrdinalIgnoreCase) ||
               (node.TypeName?.Contains(_filter, StringComparison.OrdinalIgnoreCase) ?? false) ||
               (node.ValuePreview?.Contains(_filter, StringComparison.OrdinalIgnoreCase) ?? false);
    }
}
