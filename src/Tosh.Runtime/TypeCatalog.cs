using System.Reflection;
using System.Runtime.Loader;

namespace Tosh.Runtime;

internal static class TypeCatalog
{
    public static IReadOnlyList<Assembly> GetAssemblies()
    {
        return EnumerateAssemblies()
            .OrderBy(assembly => assembly.GetName().Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static IReadOnlyList<Type> GetAllTypes(bool includeNonPublic = false)
    {
        var result = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);

        foreach (var assembly in GetAssemblies())
        {
            foreach (var type in GetAssemblyTypes(assembly, includeNonPublic))
            {
                var key = type.FullName ?? type.Name;
                result.TryAdd(key, type);
            }
        }

        return result.Values
            .OrderBy(type => type.FullName ?? type.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static IReadOnlyList<Type> GetAssemblyTypes(Assembly assembly, bool includeNonPublic = false)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        return SafeGetTypes(assembly)
            .Where(type => includeNonPublic || IsVisible(type))
            .ToArray();
    }

    private static IEnumerable<Assembly> EnumerateAssemblies()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies().Where(assembly => !assembly.IsDynamic))
        {
            if (seen.Add(assembly.FullName ?? assembly.GetName().Name ?? string.Empty))
            {
                yield return assembly;
            }
        }

        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is not string tpa ||
            string.IsNullOrWhiteSpace(tpa))
        {
            yield break;
        }

        foreach (var path in tpa.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            Assembly? assembly;

            try
            {
                var assemblyName = AssemblyName.GetAssemblyName(path);
                assembly = AppDomain.CurrentDomain
                    .GetAssemblies()
                    .FirstOrDefault(candidate => AssemblyName.ReferenceMatchesDefinition(candidate.GetName(), assemblyName))
                    ?? AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
            }
            catch
            {
                continue;
            }

            if (seen.Add(assembly.FullName ?? assembly.GetName().Name ?? string.Empty))
            {
                yield return assembly;
            }
        }
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

    private static bool IsVisible(Type type)
    {
        return type.IsPublic || type.IsNestedPublic;
    }
}
