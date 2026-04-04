using System.Reflection;
using Tosh.Core;

namespace Tosh.Cli;

internal sealed class ReplClrCompletionCatalog
{
    private static readonly object SyncRoot = new();
    private static ReplClrCompletionCatalog? _shared;
    private static int _assemblyCount = -1;

    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _childNamespaces;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<Type>> _typesByNamespace;

    private ReplClrCompletionCatalog(
        IReadOnlyDictionary<string, IReadOnlyList<string>> childNamespaces,
        IReadOnlyDictionary<string, IReadOnlyList<Type>> typesByNamespace)
    {
        _childNamespaces = childNamespaces;
        _typesByNamespace = typesByNamespace;
    }

    public static ReplClrCompletionCatalog Shared
    {
        get
        {
            lock (SyncRoot)
            {
                var currentAssemblyCount = AppDomain.CurrentDomain.GetAssemblies().Count(assembly => !assembly.IsDynamic);

                if (_shared is null || currentAssemblyCount != _assemblyCount)
                {
                    _shared = Build();
                    _assemblyCount = currentAssemblyCount;
                }

                return _shared;
            }
        }
    }

    public IReadOnlyList<ReplCompletionSuggestion> GetNamespaceAndTypeSuggestions(string qualifier, string partial)
    {
        var suggestions = new Dictionary<string, ReplCompletionSuggestion>(StringComparer.OrdinalIgnoreCase);

        if (_childNamespaces.TryGetValue(qualifier, out var children))
        {
            foreach (var child in children)
            {
                if (!MatchesPrefix(child, partial))
                {
                    continue;
                }

                suggestions[child] = new ReplCompletionSuggestion(child, "Namespace");
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

                suggestions[label] = new ReplCompletionSuggestion(
                    label,
                    type.IsEnum ? "Enum" : type.IsValueType ? "Struct" : "Type");
            }
        }

        return Order(suggestions.Values);
    }

    public IReadOnlyList<ReplCompletionSuggestion> GetImportedTypeSuggestions(string importPath, string partial)
    {
        if (!_typesByNamespace.TryGetValue(importPath, out var types))
        {
            return Array.Empty<ReplCompletionSuggestion>();
        }

        return Order(types
            .Select(type => new ReplCompletionSuggestion(
                GetTypeLabel(type),
                type.IsEnum ? "Enum" : type.IsValueType ? "Struct" : "Type"))
            .Where(item => MatchesPrefix(item.Label, partial)));
    }

    public IReadOnlyList<ReplCompletionSuggestion> GetMemberSuggestions(Type targetType, bool staticOnly, string partial)
    {
        var suggestions = new Dictionary<string, ReplCompletionSuggestion>(StringComparer.OrdinalIgnoreCase);
        var bindingFlags = BindingFlags.Public | (staticOnly ? BindingFlags.Static : BindingFlags.Instance);

        foreach (var nestedType in targetType.GetNestedTypes(bindingFlags))
        {
            if (!IsVisible(nestedType))
            {
                continue;
            }

            var label = GetTypeLabel(nestedType);

            if (!MatchesPrefix(label, partial))
            {
                continue;
            }

            suggestions[label] = new ReplCompletionSuggestion(label, "Nested type");
        }

        foreach (var property in targetType.GetProperties(bindingFlags))
        {
            if (property.GetIndexParameters().Length > 0 || !MatchesPrefix(property.Name, partial))
            {
                continue;
            }

            suggestions[property.Name] = new ReplCompletionSuggestion(property.Name, "Property");
        }

        foreach (var field in targetType.GetFields(bindingFlags))
        {
            if (field.IsSpecialName || !MatchesPrefix(field.Name, partial))
            {
                continue;
            }

            suggestions[field.Name] = new ReplCompletionSuggestion(field.Name, "Field");
        }

        foreach (var method in targetType.GetMethods(bindingFlags).Where(method => !method.IsSpecialName))
        {
            if (!MatchesPrefix(method.Name, partial))
            {
                continue;
            }

            suggestions[method.Name] = new ReplCompletionSuggestion(method.Name, "Method");
        }

        return Order(suggestions.Values);
    }

    public bool NamespaceExists(string namespaceName)
    {
        return _typesByNamespace.ContainsKey(namespaceName) || _childNamespaces.ContainsKey(namespaceName);
    }

    private static ReplClrCompletionCatalog Build()
    {
        var childNamespaces = new Dictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);
        var typesByNamespace = new Dictionary<string, List<Type>>(StringComparer.OrdinalIgnoreCase);

        foreach (var type in DotNetTypeResolver.GetKnownTypes()
                     .Concat(AppDomain.CurrentDomain.GetAssemblies()
                         .Where(assembly => !assembly.IsDynamic)
                         .SelectMany(SafeGetTypes))
                     .Distinct())
        {
            if (!IsVisible(type))
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

        return new ReplClrCompletionCatalog(
            childNamespaces.ToDictionary(
                entry => entry.Key,
                entry => (IReadOnlyList<string>)entry.Value.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray(),
                StringComparer.OrdinalIgnoreCase),
            typesByNamespace.ToDictionary(
                entry => entry.Key,
                entry => (IReadOnlyList<Type>)entry.Value.OrderBy(type => GetTypeLabel(type), StringComparer.OrdinalIgnoreCase).ToArray(),
                StringComparer.OrdinalIgnoreCase));
    }

    private static bool IsVisible(Type type)
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

    private static string GetTypeLabel(Type type)
    {
        var tickIndex = type.Name.IndexOf('`');
        return tickIndex >= 0 ? type.Name[..tickIndex] : type.Name;
    }

    private static bool MatchesPrefix(string text, string prefix)
    {
        return string.IsNullOrEmpty(prefix) ||
               text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<ReplCompletionSuggestion> Order(IEnumerable<ReplCompletionSuggestion> suggestions)
    {
        return suggestions
            .DistinctBy(item => item.Label, StringComparer.OrdinalIgnoreCase)
            .OrderBy(item => item.Label, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
