using System.Reflection;
using Tosh.Runtime;

namespace Tosh.LanguageServices;

public sealed class ClrCompletionCatalog
{
    private static readonly object SyncRoot = new();
    private static ClrCompletionCatalog? _sharedCatalog;
    private static int _sharedAssemblyCount = -1;

    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _childNamespaces;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<Type>> _typesByNamespace;

    private ClrCompletionCatalog(
        IReadOnlyDictionary<string, IReadOnlyList<string>> childNamespaces,
        IReadOnlyDictionary<string, IReadOnlyList<Type>> typesByNamespace)
    {
        _childNamespaces = childNamespaces;
        _typesByNamespace = typesByNamespace;
    }

    public static ClrCompletionCatalog Shared
    {
        get
        {
            lock (SyncRoot)
            {
                var assemblyCount = AppDomain.CurrentDomain.GetAssemblies().Count(assembly => !assembly.IsDynamic);

                if (_sharedCatalog is null || assemblyCount != _sharedAssemblyCount)
                {
                    _sharedCatalog = Build();
                    _sharedAssemblyCount = assemblyCount;
                }

                return _sharedCatalog;
            }
        }
    }

    public IReadOnlyList<LspCompletionItem> GetNamespaceAndTypeCompletions(string qualifier, string partial)
    {
        var items = new Dictionary<string, LspCompletionItem>(StringComparer.OrdinalIgnoreCase);

        if (_childNamespaces.TryGetValue(qualifier, out var childNamespaces))
        {
            foreach (var childNamespace in childNamespaces)
            {
                if (!MatchesPrefix(childNamespace, partial))
                {
                    continue;
                }

                items[childNamespace] = new LspCompletionItem(
                    childNamespace,
                    Kind: 9,
                    Detail: "Namespace",
                    Documentation: string.IsNullOrWhiteSpace(qualifier)
                        ? childNamespace
                        : qualifier + "." + childNamespace);
            }
        }

        if (_typesByNamespace.TryGetValue(qualifier, out var types))
        {
            foreach (var type in types)
            {
                var label = GetTypeLabel(type);

                if (!MatchesPrefix(label, partial))
                {
                    continue;
                }

                items[label] = new LspCompletionItem(
                    label,
                    Kind: 7,
                    Detail: type.IsEnum ? "Enum" : type.IsValueType ? "Struct" : "Type",
                    Documentation: ClrMetadataFormatting.FormatTypeDisplayName(type));
            }
        }

        return Order(items.Values);
    }

    public IReadOnlyList<LspCompletionItem> GetImportedTypeCompletions(IEnumerable<string> imports, string partial)
    {
        var items = new Dictionary<string, LspCompletionItem>(StringComparer.OrdinalIgnoreCase);

        foreach (var importPath in imports.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!_typesByNamespace.TryGetValue(importPath, out var types))
            {
                continue;
            }

            foreach (var type in types)
            {
                var label = GetTypeLabel(type);

                if (!MatchesPrefix(label, partial))
                {
                    continue;
                }

                items[label] = new LspCompletionItem(
                    label,
                    Kind: 7,
                    Detail: type.IsEnum ? "Enum" : type.IsValueType ? "Struct" : "Type",
                    Documentation: ClrMetadataFormatting.FormatTypeDisplayName(type));
            }
        }

        return Order(items.Values);
    }

    public IReadOnlyList<LspCompletionItem> GetAliasCompletions(IEnumerable<KeyValuePair<string, string>> aliases, string partial, Func<string, Type?> resolvePath)
    {
        var items = new Dictionary<string, LspCompletionItem>(StringComparer.OrdinalIgnoreCase);

        foreach (var (alias, targetPath) in aliases)
        {
            if (!MatchesPrefix(alias, partial))
            {
                continue;
            }

            var resolvedType = resolvePath(targetPath);

            items[alias] = new LspCompletionItem(
                alias,
                Kind: resolvedType is null ? 9 : 7,
                Detail: resolvedType is null ? "CLR alias" : "Type alias",
                Documentation: targetPath);
        }

        return Order(items.Values);
    }

    public IReadOnlyList<LspCompletionItem> GetBuiltInAliasCompletions(string partial)
    {
        var items = new Dictionary<string, LspCompletionItem>(StringComparer.OrdinalIgnoreCase);

        foreach (var (alias, type) in DotNetTypeResolver.BuiltInAliases)
        {
            if (!MatchesPrefix(alias, partial))
            {
                continue;
            }

            items[alias] = new LspCompletionItem(
                alias,
                Kind: 7,
                Detail: "CLR alias",
                Documentation: ClrMetadataFormatting.FormatTypeDisplayName(type));
        }

        return Order(items.Values);
    }

    public IReadOnlyList<LspCompletionItem> GetMemberCompletions(Type targetType, bool staticOnly, string partial)
    {
        var items = new Dictionary<string, LspCompletionItem>(StringComparer.OrdinalIgnoreCase);
        var bindingFlags = BindingFlags.Public | (staticOnly ? BindingFlags.Static : BindingFlags.Instance);

        foreach (var nestedType in targetType.GetNestedTypes(bindingFlags))
        {
            if (!IsTypeVisible(nestedType))
            {
                continue;
            }

            var label = GetTypeLabel(nestedType);

            if (!MatchesPrefix(label, partial))
            {
                continue;
            }

            items[label] = new LspCompletionItem(
                label,
                Kind: 7,
                Detail: "Nested type",
                Documentation: ClrMetadataFormatting.FormatTypeDisplayName(nestedType));
        }

        foreach (var property in targetType.GetProperties(bindingFlags))
        {
            if (!MatchesPrefix(property.Name, partial) || property.GetIndexParameters().Length > 0)
            {
                continue;
            }

            items[property.Name] = new LspCompletionItem(
                property.Name,
                Kind: 10,
                Detail: "Property",
                Documentation: $"{ClrMetadataFormatting.FormatTypeDisplayName(property.PropertyType)} {property.Name}");
        }

        foreach (var field in targetType.GetFields(bindingFlags))
        {
            if (!MatchesPrefix(field.Name, partial) || field.IsSpecialName)
            {
                continue;
            }

            items[field.Name] = new LspCompletionItem(
                field.Name,
                Kind: 5,
                Detail: "Field",
                Documentation: $"{ClrMetadataFormatting.FormatTypeDisplayName(field.FieldType)} {field.Name}");
        }

        foreach (var methodGroup in targetType.GetMethods(bindingFlags)
                     .Where(method => !method.IsSpecialName)
                     .GroupBy(method => method.Name, StringComparer.Ordinal))
        {
            if (!MatchesPrefix(methodGroup.Key, partial))
            {
                continue;
            }

            var overloads = methodGroup
                .OrderBy(method => method.GetParameters().Length)
                .ToArray();
            var detail = overloads.Length == 1 ? "Method" : $"Method ({overloads.Length} overloads)";
            var documentation = string.Join(
                "\n",
                overloads.Take(3).Select(ClrMetadataFormatting.FormatMethodSignature));

            items[methodGroup.Key] = new LspCompletionItem(
                methodGroup.Key,
                Kind: 2,
                Detail: detail,
                Documentation: documentation);
        }

        return Order(items.Values);
    }

    public bool NamespaceExists(string namespaceName) => _typesByNamespace.ContainsKey(namespaceName) || _childNamespaces.ContainsKey(namespaceName);

    private static ClrCompletionCatalog Build()
    {
        var childNamespaces = new Dictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);
        var typesByNamespace = new Dictionary<string, List<Type>>(StringComparer.OrdinalIgnoreCase);

        foreach (var type in DotNetTypeResolver.GetKnownTypes()
                     .Concat(AppDomain.CurrentDomain.GetAssemblies()
                         .Where(assembly => !assembly.IsDynamic)
                         .SelectMany(SafeGetTypes))
                     .Distinct())
        {
            if (!IsTypeVisible(type))
            {
                continue;
            }

            var namespaceName = type.Namespace ?? string.Empty;
            RegisterNamespaceHierarchy(childNamespaces, namespaceName);

            if (!typesByNamespace.TryGetValue(namespaceName, out var types))
            {
                types = new List<Type>();
                typesByNamespace[namespaceName] = types;
            }

            if (!types.Contains(type))
            {
                types.Add(type);
            }
        }

        return new ClrCompletionCatalog(
            childNamespaces.ToDictionary(
                entry => entry.Key,
                entry => (IReadOnlyList<string>)entry.Value.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray(),
                StringComparer.OrdinalIgnoreCase),
            typesByNamespace.ToDictionary(
                entry => entry.Key,
                entry => (IReadOnlyList<Type>)entry.Value
                    .OrderBy(type => GetTypeLabel(type), StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                StringComparer.OrdinalIgnoreCase));
    }

    private static bool IsTypeVisible(Type type)
    {
        return (type.IsPublic || type.IsNestedPublic) &&
               !type.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), inherit: false) &&
               !type.Name.Contains('<', StringComparison.Ordinal) &&
               !string.IsNullOrWhiteSpace(type.Namespace);
    }

    private static IEnumerable<Type> SafeGetTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            return exception.Types.Where(type => type is not null)!;
        }
    }

    private static void RegisterNamespaceHierarchy(IDictionary<string, SortedSet<string>> childNamespaces, string namespaceName)
    {
        if (string.IsNullOrWhiteSpace(namespaceName))
        {
            return;
        }

        var segments = namespaceName.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var current = string.Empty;

        foreach (var segment in segments)
        {
            if (!childNamespaces.TryGetValue(current, out var children))
            {
                children = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                childNamespaces[current] = children;
            }

            children.Add(segment);
            current = string.IsNullOrEmpty(current) ? segment : current + "." + segment;
        }
    }

    private static IReadOnlyList<LspCompletionItem> Order(IEnumerable<LspCompletionItem> items)
    {
        return items
            .OrderBy(item => item.Label, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool MatchesPrefix(string text, string prefix)
    {
        return string.IsNullOrEmpty(prefix) ||
               text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetTypeLabel(Type type)
    {
        var name = type.Name;
        var tickIndex = name.IndexOf('`');
        return tickIndex >= 0 ? name[..tickIndex] : name;
    }

}
