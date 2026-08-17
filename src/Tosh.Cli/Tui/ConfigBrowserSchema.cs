global using ConfigBrowseRequest = Tosh.Tui.Requests.ConfigBrowseRequest;

using System.Reflection;
using System.Text;

using Tosh.Runtime;

namespace Tosh.Cli.Tui;

public enum ConfigBrowserNodeKind
{
    Group,
    Value,
}

public enum ConfigBrowserEditorKind
{
    Group,
    Boolean,
    Enum,
    Number,
    Text,
    Path,
    Collection,
    Unsupported,
}

public sealed record ConfigBrowserNode(
    string Id,
    string Name,
    string DisplayName,
    string Path,
    ConfigBrowserNodeKind Kind,
    ConfigBrowserEditorKind EditorKind,
    Type ValueType,
    string TypeName,
    bool IsNullable,
    bool IsResettable,
    bool IsEditable,
    IReadOnlyList<ConfigBrowserNode> Children);

public sealed record ConfigBrowserSchema(
    ConfigBrowserNode Root,
    ToshConfig DefaultConfig,
    IReadOnlyDictionary<string, ConfigBrowserNode> NodesByPath);

public static class ConfigBrowserSchemaBuilder
{
    public static ConfigBrowserSchema Build(ToshRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);

        var defaultRuntime = ToshRuntime.CreateDefault(TextWriter.Null, TextWriter.Null);
        var nodesByPath = new Dictionary<string, ConfigBrowserNode>(StringComparer.OrdinalIgnoreCase);
        var root = BuildNode(
            currentValue: runtime.Config,
            defaultValue: defaultRuntime.Config,
            name: nameof(ToshConfig),
            path: string.Empty,
            nodesByPath);

        return new ConfigBrowserSchema(root, defaultRuntime.Config, nodesByPath);
    }

    private static ConfigBrowserNode BuildNode(
        object? currentValue,
        object? defaultValue,
        string name,
        string path,
        Dictionary<string, ConfigBrowserNode> nodesByPath)
    {
        var valueType = currentValue?.GetType() ?? defaultValue?.GetType() ?? typeof(object);
        var editorKind = ClassifyEditorKind(valueType, name);
        var children = editorKind == ConfigBrowserEditorKind.Group
            ? BuildChildren(currentValue, defaultValue, path, nodesByPath)
            : Array.Empty<ConfigBrowserNode>();
        var kind = children.Count > 0 || editorKind == ConfigBrowserEditorKind.Group
            ? ConfigBrowserNodeKind.Group
            : ConfigBrowserNodeKind.Value;
        var nullableValueType = Nullable.GetUnderlyingType(valueType);
        var isNullable = nullableValueType is not null || !valueType.IsValueType;
        var effectiveType = nullableValueType ?? valueType;
        var isEditable = kind == ConfigBrowserNodeKind.Value &&
                         editorKind is ConfigBrowserEditorKind.Boolean or
                             ConfigBrowserEditorKind.Enum or
                             ConfigBrowserEditorKind.Number or
                             ConfigBrowserEditorKind.Text or
                             ConfigBrowserEditorKind.Path ||
                         kind == ConfigBrowserNodeKind.Value &&
                         editorKind == ConfigBrowserEditorKind.Collection &&
                         ConfigCollectionEditorRegistry.SupportsEditing(path, effectiveType);
        var node = new ConfigBrowserNode(
            Id: path.Length == 0 ? name : path,
            Name: name,
            DisplayName: ToDisplayName(name),
            Path: path,
            Kind: kind,
            EditorKind: kind == ConfigBrowserNodeKind.Group ? ConfigBrowserEditorKind.Group : editorKind,
            ValueType: effectiveType,
            TypeName: GetTypeDisplayName(effectiveType),
            IsNullable: isNullable,
            IsResettable: currentValue is IResettableShellConfig || defaultValue is IResettableShellConfig,
            IsEditable: isEditable,
            Children: children);

        nodesByPath[path] = node;
        return node;
    }

    private static IReadOnlyList<ConfigBrowserNode> BuildChildren(
        object? currentValue,
        object? defaultValue,
        string parentPath,
        Dictionary<string, ConfigBrowserNode> nodesByPath)
    {
        var type = currentValue?.GetType() ?? defaultValue?.GetType();

        if (type is null)
        {
            return Array.Empty<ConfigBrowserNode>();
        }

        var properties = type
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.GetIndexParameters().Length == 0 && property.CanRead)
            .OrderBy(property => property.MetadataToken)
            .ToArray();

        var children = new List<ConfigBrowserNode>(properties.Length);

        foreach (var property in properties)
        {
            var currentChild = TryGetPropertyValue(currentValue, property);
            var defaultChild = TryGetPropertyValue(defaultValue, property);
            var childPath = parentPath.Length == 0 ? property.Name : $"{parentPath}.{property.Name}";
            children.Add(BuildNode(currentChild, defaultChild, property.Name, childPath, nodesByPath));
        }

        return children;
    }

    private static object? TryGetPropertyValue(object? target, PropertyInfo property)
    {
        if (target is null)
        {
            return null;
        }

        try
        {
            return property.GetValue(target);
        }
        catch
        {
            return null;
        }
    }

    private static ConfigBrowserEditorKind ClassifyEditorKind(Type valueType, string name)
    {
        var effectiveType = Nullable.GetUnderlyingType(valueType) ?? valueType;

        if (typeof(IResettableShellConfig).IsAssignableFrom(effectiveType))
        {
            return ConfigBrowserEditorKind.Group;
        }

        if (effectiveType == typeof(string))
        {
            return name.Contains("Path", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("Directory", StringComparison.OrdinalIgnoreCase)
                ? ConfigBrowserEditorKind.Path
                : ConfigBrowserEditorKind.Text;
        }

        if (effectiveType == typeof(bool))
        {
            return ConfigBrowserEditorKind.Boolean;
        }

        if (effectiveType.IsEnum)
        {
            return ConfigBrowserEditorKind.Enum;
        }

        if (IsNumericType(effectiveType))
        {
            return ConfigBrowserEditorKind.Number;
        }

        if (IsCollectionType(effectiveType))
        {
            return ConfigBrowserEditorKind.Collection;
        }

        if (HasBrowsableChildren(effectiveType))
        {
            return ConfigBrowserEditorKind.Group;
        }

        return ConfigBrowserEditorKind.Unsupported;
    }

    private static bool HasBrowsableChildren(Type type)
    {
        if (type == typeof(string) || IsCollectionType(type))
        {
            return false;
        }

        return type
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Any(property => property.CanRead && property.GetIndexParameters().Length == 0);
    }

    private static bool IsCollectionType(Type type)
    {
        return typeof(System.Collections.IEnumerable).IsAssignableFrom(type) && type != typeof(string);
    }

    private static bool IsNumericType(Type type)
    {
        return type == typeof(byte) ||
               type == typeof(sbyte) ||
               type == typeof(short) ||
               type == typeof(ushort) ||
               type == typeof(int) ||
               type == typeof(uint) ||
               type == typeof(long) ||
               type == typeof(ulong) ||
               type == typeof(float) ||
               type == typeof(double) ||
               type == typeof(decimal);
    }

    private static string GetTypeDisplayName(Type type)
    {
        if (!type.IsGenericType)
        {
            return type.Name;
        }

        var name = type.Name;
        var tickIndex = name.IndexOf('`');
        if (tickIndex >= 0)
        {
            name = name[..tickIndex];
        }

        var genericArguments = type.GetGenericArguments()
            .Select(GetTypeDisplayName);
        return $"{name}<{string.Join(", ", genericArguments)}>";
    }

    private static string ToDisplayName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return name;
        }

        var builder = new StringBuilder(name.Length + 8);

        for (var index = 0; index < name.Length; index++)
        {
            var character = name[index];

            if (index > 0 &&
                char.IsUpper(character) &&
                (char.IsLower(name[index - 1]) ||
                 (index + 1 < name.Length && char.IsLower(name[index + 1]))))
            {
                builder.Append(' ');
            }

            builder.Append(character);
        }

        return builder.ToString();
    }
}
