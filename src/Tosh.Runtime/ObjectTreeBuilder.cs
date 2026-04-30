using System.Collections;
using System.Net;
using System.Reflection;

namespace Tosh.Runtime;

public sealed class ObjectTreeBuilder
{
    private const int DefaultPreviewLimit = 12;
    private const int DefaultItemPreviewLimit = 8;
    private const int MaxExpansionDepth = 4;

    private readonly ObjectFormatter _formatter;

    public ObjectTreeBuilder(ObjectFormatter formatter)
    {
        _formatter = formatter ?? throw new ArgumentNullException(nameof(formatter));
    }

    public InspectTreeFrame BuildFrame(object? value, bool includeAllMembers = false, IReadOnlyList<string>? breadcrumb = null, string? rootExpression = null)
    {
        breadcrumb ??= [GetRootBreadcrumbLabel(value)];
        var description = DescribeValue(value);
        var ancestors = new List<object>();

        if (ShouldTrackReference(value))
        {
            ancestors.Add(value!);
        }

        var nodes = BuildNodesForValue(value, includeAllMembers, ancestors, depth: 0);
        var summaryMemberCount = nodes.Sum(GetSummaryNodeCount);

        return new InspectTreeFrame(
            value,
            rootExpression,
            description.TypeName,
            description.AssemblyName,
            description.BaseTypeName,
            PreviewValue(value),
            breadcrumb.ToArray(),
            nodes,
            summaryMemberCount);
    }

    private IReadOnlyList<InspectTreeNode> BuildNodesForValue(
        object? value,
        bool includeAllMembers,
        IReadOnlyList<object> ancestors,
        int depth)
    {
        if (value is null)
        {
            return
            [
                new InspectTreeNode(InspectTreeNodeKind.Message, "<null>")
            ];
        }

        var nodes = new List<InspectTreeNode>();

        var propertyNodes = BuildPropertyNodes(value, includeAllMembers, ancestors, depth);
        if (propertyNodes.Count > 0)
        {
            nodes.Add(CreateSectionNode("Properties", propertyNodes, expandedByDefault: true, totalCount: GetActualNodeCount(propertyNodes)));
        }

        var fieldNodes = BuildFieldNodes(value, includeAllMembers, ancestors, depth);
        if (fieldNodes.Count > 0)
        {
            nodes.Add(CreateSectionNode("Fields", fieldNodes, expandedByDefault: true, totalCount: GetActualNodeCount(fieldNodes)));
        }

        var methodNodes = BuildMethodNodes(value, includeAllMembers);
        if (methodNodes.Count > 0)
        {
            nodes.Add(CreateSectionNode("Methods", methodNodes, expandedByDefault: false, totalCount: GetActualNodeCount(methodNodes)));
        }

        var interfaceNodes = BuildInterfaceNodes(value);
        if (interfaceNodes.Count > 0)
        {
            nodes.Add(CreateSectionNode("Interfaces", interfaceNodes, expandedByDefault: false, totalCount: GetActualNodeCount(interfaceNodes)));
        }

        var itemNodes = BuildItemNodes(value, includeAllMembers, ancestors, depth);
        if (itemNodes.Count > 0)
        {
            nodes.Add(CreateSectionNode("Items", itemNodes, expandedByDefault: false, totalCount: GetActualNodeCount(itemNodes)));
        }

        if (nodes.Count == 0)
        {
            nodes.Add(new InspectTreeNode(InspectTreeNodeKind.Message, "<no inspectable members>"));
        }

        return nodes;
    }

    private IReadOnlyList<InspectTreeNode> BuildPropertyNodes(
        object value,
        bool includeAllMembers,
        IReadOnlyList<object> ancestors,
        int depth)
    {
        if (ShellRecordUtilities.TryGetFields(value, out var recordFields))
        {
            return recordFields
                .Select(field => CreateValueNode(
                    InspectTreeNodeKind.Property,
                    field.Key,
                    field.Value,
                    includeAllMembers,
                    ancestors,
                    depth))
                .ToArray();
        }

        var flags = GetMemberFlags(includeAllMembers);
        var runtimeType = value.GetType();

        var properties = runtimeType
            .GetProperties(flags)
            .Where(property =>
                property.GetIndexParameters().Length == 0 &&
                property.GetMethod is not null)
            .OrderBy(property => property.Name, StringComparer.Ordinal)
            .ToArray();

        return CreateTruncatedNodes(
            properties,
            property =>
            {
                object? propertyValue;
                Type propertyType;

                if (ObjectMemberAdapter.TryGetMember(runtimeType, property.Name, out var adapted))
                {
                    propertyType = adapted.ValueType;
                    propertyValue = ObjectMemberAdapter.SafeGetValue(value, property.Name);
                }
                else
                {
                    propertyType = property.PropertyType;
                    propertyValue = SafeGetValue(property, value);
                }

                return CreateValueNode(
                    InspectTreeNodeKind.Property,
                    property.Name,
                    propertyValue,
                    includeAllMembers,
                    ancestors,
                    depth,
                    explicitType: propertyType);
            },
            includeAllMembers ? int.MaxValue : DefaultPreviewLimit);
    }

    private IReadOnlyList<InspectTreeNode> BuildFieldNodes(
        object value,
        bool includeAllMembers,
        IReadOnlyList<object> ancestors,
        int depth)
    {
        var flags = GetMemberFlags(includeAllMembers);
        var runtimeType = value.GetType();

        var fields = runtimeType
            .GetFields(flags)
            .Where(field => !field.IsSpecialName)
            .OrderBy(field => field.Name, StringComparer.Ordinal)
            .ToArray();

        return CreateTruncatedNodes(
            fields,
            field => CreateValueNode(
                InspectTreeNodeKind.Field,
                field.Name,
                SafeGetValue(field, value),
                includeAllMembers,
                ancestors,
                depth,
                explicitType: field.FieldType),
            includeAllMembers ? int.MaxValue : DefaultPreviewLimit);
    }

    private IReadOnlyList<InspectTreeNode> BuildMethodNodes(object value, bool includeAllMembers)
    {
        var flags = GetMemberFlags(includeAllMembers);
        var runtimeType = value.GetType();
        var methods = runtimeType
            .GetMethods(flags)
            .Where(method => !method.IsSpecialName)
            .OrderBy(method => method.Name, StringComparer.Ordinal)
            .ThenBy(method => method.GetParameters().Length)
            .Select(ReflectionMetadataUtilities.FormatMethodSignature)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return CreateTruncatedLeafNodes(
            methods,
            text => new InspectTreeNode(
                InspectTreeNodeKind.Method,
                text,
                insertionSegment: InspectInsertionUtilities.BuildInsertionSegment(InspectTreeNodeKind.Method, text)),
            includeAllMembers ? int.MaxValue : DefaultPreviewLimit);
    }

    private IReadOnlyList<InspectTreeNode> BuildInterfaceNodes(object value)
    {
        var runtimeType = value.GetType();
        var interfaces = runtimeType
            .GetInterfaces()
            .Select(ReflectionMetadataUtilities.GetDisplayName)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        return CreateTruncatedLeafNodes(
            interfaces,
            text => new InspectTreeNode(
                InspectTreeNodeKind.Interface,
                text,
                insertionSegment: InspectInsertionUtilities.BuildInsertionSegment(InspectTreeNodeKind.Interface, text)),
            DefaultPreviewLimit);
    }

    private IReadOnlyList<InspectTreeNode> BuildItemNodes(
        object value,
        bool includeAllMembers,
        IReadOnlyList<object> ancestors,
        int depth)
    {
        if (value is string || value is not IEnumerable enumerable)
        {
            return Array.Empty<InspectTreeNode>();
        }

        var items = new List<InspectTreeNode>();
        var index = 0;

        foreach (var item in enumerable)
        {
            if (items.Count >= DefaultItemPreviewLimit)
            {
                var remaining = TryGetCount(value) is int count ? Math.Max(0, count - items.Count) : 0;
                items.Add(new InspectTreeNode(
                    InspectTreeNodeKind.Ellipsis,
                    remaining > 0 ? $"... ({remaining} more)" : "... (more)",
                    childrenFactory: () => BuildRemainingItemNodes(value, includeAllMembers, ancestors, depth, items.Count)));
                break;
            }

            items.Add(CreateValueNode(
                InspectTreeNodeKind.Item,
                $"[{index}]",
                item,
                includeAllMembers,
                ancestors,
                depth));
            index++;
        }

        return items;
    }

    private IReadOnlyList<InspectTreeNode> BuildRemainingItemNodes(
        object value,
        bool includeAllMembers,
        IReadOnlyList<object> ancestors,
        int depth,
        int skip)
    {
        if (value is string || value is not IEnumerable enumerable)
        {
            return Array.Empty<InspectTreeNode>();
        }

        var items = new List<InspectTreeNode>();
        var index = 0;

        foreach (var item in enumerable)
        {
            if (index++ < skip)
            {
                continue;
            }

            items.Add(CreateValueNode(
                InspectTreeNodeKind.Item,
                $"[{index - 1}]",
                item,
                includeAllMembers,
                ancestors,
                depth));
        }

        return items;
    }

    private InspectTreeNode CreateValueNode(
        InspectTreeNodeKind kind,
        string name,
        object? value,
        bool includeAllMembers,
        IReadOnlyList<object> ancestors,
        int depth,
        Type? explicitType = null)
    {
        var runtimeType = explicitType ?? value?.GetType();
        var typeName = runtimeType is null ? "null" : ObjectFormatter.GetTypeName(runtimeType);
        var preview = PreviewValue(value);
        var canExpand = CanExpandValue(value, depth, ancestors);
        var breadcrumbLabel = name.Trim('[', ']');

        return new InspectTreeNode(
            kind,
            name,
            typeName,
            preview,
            inspectValue: canExpand ? value : null,
            breadcrumbLabel: breadcrumbLabel,
            insertionSegment: InspectInsertionUtilities.BuildInsertionSegment(kind, name),
            childrenFactory: canExpand
                ? () => BuildExpandedValueNodes(value, includeAllMembers, ancestors, depth + 1)
                : null);
    }

    private IReadOnlyList<InspectTreeNode> BuildExpandedValueNodes(
        object? value,
        bool includeAllMembers,
        IReadOnlyList<object> ancestors,
        int depth)
    {
        if (value is null)
        {
            return [new InspectTreeNode(InspectTreeNodeKind.Message, "<null>")];
        }

        if (depth >= MaxExpansionDepth)
        {
            return [new InspectTreeNode(InspectTreeNodeKind.Message, "<max depth>")];
        }

        if (ShouldTrackReference(value) && ancestors.Contains(value, ReferenceEqualityComparer.Instance))
        {
            return [new InspectTreeNode(InspectTreeNodeKind.Message, "<circular>")];
        }

        var nextAncestors = new List<object>(ancestors);

        if (ShouldTrackReference(value))
        {
            nextAncestors.Add(value);
        }

        return BuildNodesForValue(value, includeAllMembers, nextAncestors, depth);
    }

    private InspectTreeNode CreateSectionNode(string title, IReadOnlyList<InspectTreeNode> children, bool expandedByDefault, int totalCount)
    {
        return new InspectTreeNode(
            InspectTreeNodeKind.Section,
            title,
            count: totalCount,
            isExpanded: expandedByDefault,
            childrenFactory: () => children);
    }

    private IReadOnlyList<InspectTreeNode> CreateTruncatedLeafNodes<T>(
        IReadOnlyList<T> values,
        Func<T, InspectTreeNode> factory,
        int limit)
    {
        return CreateTruncatedNodes(values, factory, limit);
    }

    private IReadOnlyList<InspectTreeNode> CreateTruncatedNodes<T>(
        IReadOnlyList<T> values,
        Func<T, InspectTreeNode> factory,
        int limit)
    {
        if (values.Count <= limit)
        {
            return values.Select(factory).ToArray();
        }

        var head = values.Take(limit).Select(factory).ToList();
        var remaining = values.Skip(limit).ToArray();
        head.Add(new InspectTreeNode(
            InspectTreeNodeKind.Ellipsis,
            $"... ({remaining.Length} more)",
            childrenFactory: () => remaining.Select(factory).ToArray()));
        return head;
    }

    private static object? SafeGetValue(PropertyInfo property, object target)
    {
        try
        {
            return property.GetValue(target);
        }
        catch (Exception exception)
        {
            return $"<unavailable: {exception.GetType().Name}>";
        }
    }

    private static object? SafeGetValue(FieldInfo field, object target)
    {
        try
        {
            return field.GetValue(target);
        }
        catch (Exception exception)
        {
            return $"<unavailable: {exception.GetType().Name}>";
        }
    }

    private string PreviewValue(object? value)
    {
        if (value is null)
        {
            return "null";
        }

        var options = new ObjectFormattingOptions(ObjectRenderStyle.Compact, MaxDepth: 1, MaxCollectionItemCount: 4, MaxPropertyCount: 4);
        return _formatter.Format(value, options);
    }

    private static bool CanExpandValue(object? value, int depth, IReadOnlyList<object> ancestors)
    {
        if (value is null || depth >= MaxExpansionDepth || IsSimpleValue(value))
        {
            return false;
        }

        if (ShouldTrackReference(value) && ancestors.Contains(value, ReferenceEqualityComparer.Instance))
        {
            return true;
        }

        if (value is IEnumerable enumerable and not string)
        {
            if (TryGetCount(value) is > 0)
            {
                return true;
            }

            var enumerator = enumerable.GetEnumerator();

            try
            {
                return enumerator.MoveNext();
            }
            finally
            {
                (enumerator as IDisposable)?.Dispose();
            }
        }

        return true;
    }

    private static bool IsSimpleValue(object value)
    {
        if (ObjectFormatter.TryFormatSimple(value, isRoot: true, out _))
        {
            return true;
        }

        var type = Nullable.GetUnderlyingType(value.GetType()) ?? value.GetType();
        return type == typeof(DateOnly) ||
               type == typeof(TimeOnly) ||
               type == typeof(StorageSize) ||
               type == typeof(TemporalAmount) ||
               type == typeof(IPAddress) ||
               type == typeof(UnixFileMode) ||
               type == typeof(FileAttributes) ||
               type == typeof(FileSystemPrincipalInfo) ||
               type == typeof(FileSystemEntryType) ||
               type == typeof(ShellJobStatus) ||
               type == typeof(HelpSubjectKind) ||
               type == typeof(CommandResolutionKind);
    }

    private static bool ShouldTrackReference(object? value)
    {
        return value is not null &&
               !value.GetType().IsValueType &&
               value is not string;
    }

    private static BindingFlags GetMemberFlags(bool includeAllMembers)
    {
        return includeAllMembers
            ? BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic
            : BindingFlags.Instance | BindingFlags.Public;
    }

    private static string GetRootBreadcrumbLabel(object? value)
    {
        return value is null ? "null" : DescribeValue(value).ShortTypeName;
    }

    private static (string TypeName, string ShortTypeName, string? AssemblyName, string? BaseTypeName) DescribeValue(object? value)
    {
        if (value is null)
        {
            return ("null", "null", null, null);
        }

        if (value is IShellTypeDescriptor descriptor)
        {
            return (descriptor.ShellTypeName, descriptor.ShellTypeName, descriptor.ShellAssemblyName, descriptor.ShellBaseTypeName);
        }

        if (value is IShellTypedObject typed)
        {
            var typedDescriptor = typed.ShellTypeDescriptor;
            return (typedDescriptor.ShellTypeName, typedDescriptor.ShellTypeName, typedDescriptor.ShellAssemblyName, typedDescriptor.ShellBaseTypeName);
        }

        if (value is IShellRecordObject shellRecord)
        {
            return (shellRecord.ShellTypeName, shellRecord.ShellTypeName, "ToSh", "System.Object");
        }

        if (BuiltInShellTypes.TryDescribeRuntimeValue(value, out var builtInDescriptor))
        {
            return (builtInDescriptor.ShellTypeName, builtInDescriptor.ShellTypeName, builtInDescriptor.ShellAssemblyName, builtInDescriptor.ShellBaseTypeName);
        }

        var runtimeType = value.GetType();
        return (
            ReflectionMetadataUtilities.GetDisplayName(runtimeType),
            ObjectFormatter.GetTypeName(runtimeType),
            runtimeType.Assembly.GetName().Name,
            runtimeType.BaseType is null ? null : ReflectionMetadataUtilities.GetDisplayName(runtimeType.BaseType));
    }

    private static int? TryGetCount(object value)
    {
        return value switch
        {
            Array array => array.Length,
            ICollection collection => collection.Count,
            _ => TryGetGenericCount(value),
        };
    }

    private static int? TryGetGenericCount(object value)
    {
        var runtimeType = value.GetType();
        var countProperty = runtimeType
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .FirstOrDefault(property =>
                property.Name == "Count" &&
                property.PropertyType == typeof(int) &&
                property.GetIndexParameters().Length == 0);

        if (countProperty is null)
        {
            return null;
        }

        try
        {
            return (int?)countProperty.GetValue(value);
        }
        catch
        {
            return null;
        }
    }

    private static int GetSummaryNodeCount(InspectTreeNode node)
    {
        return node.Count ?? node.GetChildren().Count;
    }

    private static int GetActualNodeCount(IReadOnlyList<InspectTreeNode> nodes)
    {
        return nodes.LastOrDefault(static node => node.Kind == InspectTreeNodeKind.Ellipsis) is { Text: var text } &&
               TryGetEllipsisRemaining(text, out var remaining)
            ? nodes.Count - 1 + remaining
            : nodes.Count;
    }

    private static bool TryGetEllipsisRemaining(string text, out int remaining)
    {
        remaining = 0;

        var start = text.IndexOf('(');
        var end = text.IndexOf(')');

        if (start < 0 || end <= start)
        {
            return false;
        }

        var content = text[(start + 1)..end];
        var space = content.IndexOf(' ');

        if (space > 0)
        {
            content = content[..space];
        }

        return int.TryParse(content, out remaining);
    }
}
