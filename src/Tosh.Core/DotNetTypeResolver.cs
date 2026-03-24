using System.Reflection;
using System.Runtime.Loader;

namespace Tosh.Core;

public sealed class DotNetTypeResolver : IImportingTypeResolver
{
    private static readonly IReadOnlyDictionary<string, Type> Aliases = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase)
    {
        ["bool"] = typeof(bool),
        ["byte"] = typeof(byte),
        ["char"] = typeof(char),
        ["datetime"] = typeof(DateTime),
        ["decimal"] = typeof(decimal),
        ["double"] = typeof(double),
        ["file"] = typeof(FileInfo),
        ["float"] = typeof(float),
        ["guid"] = typeof(Guid),
        ["int"] = typeof(int),
        ["long"] = typeof(long),
        ["object"] = typeof(object),
        ["short"] = typeof(short),
        ["string"] = typeof(string),
        ["timespan"] = typeof(TimeSpan),
        ["uri"] = typeof(Uri),
    };
    private static readonly Lazy<PlatformTypeIndex> PlatformTypes = new(BuildPlatformTypeIndex);

    private readonly Dictionary<string, string> _aliases = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _imports = new(StringComparer.OrdinalIgnoreCase);

    public void AddUsing(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _imports.Add(path);
    }

    public void AddAlias(string alias, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(alias);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _aliases[alias] = path;
    }

    public Type? Resolve(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (Aliases.TryGetValue(name, out var alias))
        {
            return alias;
        }

        if (TryResolveAliasedPath(name, out var aliasedPath) &&
            TryResolveDirect(aliasedPath, out var aliasedType))
        {
            return aliasedType;
        }

        if (TryResolveDirect(name, out var direct))
        {
            return direct;
        }

        if (TryResolveFromImports(name, out var importedType))
        {
            return importedType;
        }

        return null;
    }

    private bool TryResolveAliasedPath(string name, out string expandedPath)
    {
        foreach (var (alias, targetPath) in _aliases)
        {
            if (string.Equals(name, alias, StringComparison.OrdinalIgnoreCase))
            {
                expandedPath = targetPath;
                return true;
            }

            if (name.StartsWith(alias + ".", StringComparison.OrdinalIgnoreCase))
            {
                expandedPath = targetPath + name[alias.Length..];
                return true;
            }
        }

        expandedPath = string.Empty;
        return false;
    }

    private bool TryResolveFromImports(string name, out Type? type)
    {
        foreach (var importPath in _imports)
        {
            if (string.Equals(GetLastSegment(importPath), name, StringComparison.OrdinalIgnoreCase) &&
                TryResolveDirect(importPath, out type))
            {
                return true;
            }

            if (TryResolveDirect(importPath + "." + name, out type))
            {
                return true;
            }
        }

        type = null;
        return false;
    }

    private static bool TryResolveDirect(string name, out Type? type)
    {
        type = Type.GetType(name, throwOnError: false, ignoreCase: true);

        if (type is not null)
        {
            return true;
        }

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies().Where(assembly => !assembly.IsDynamic))
        {
            var match = SafeGetTypes(assembly).FirstOrDefault(type =>
                TypeNameMatches(type.FullName, name) ||
                TypeNameMatches(type.Name, name));

            if (match is not null)
            {
                type = match;
                return true;
            }
        }

        if (PlatformTypes.Value.TryGet(name, out type))
        {
            return true;
        }

        type = null;
        return false;
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

    private static string GetLastSegment(string path)
    {
        var separatorIndex = path.LastIndexOf('.');
        return separatorIndex >= 0 ? path[(separatorIndex + 1)..] : path;
    }

    private static bool TypeNameMatches(string? candidate, string requested)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        return string.Equals(candidate, requested, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(StripGenericArity(candidate), requested, StringComparison.OrdinalIgnoreCase);
    }

    private static string StripGenericArity(string name)
    {
        var tickIndex = name.IndexOf('`');
        return tickIndex >= 0 ? name[..tickIndex] : name;
    }

    private static PlatformTypeIndex BuildPlatformTypeIndex()
    {
        var fullNames = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);
        var simpleNames = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);

        foreach (var assembly in EnumerateTrustedPlatformAssemblies())
        {
            foreach (var type in SafeGetTypes(assembly))
            {
                RegisterType(fullNames, simpleNames, type);
            }
        }

        return new PlatformTypeIndex(fullNames, simpleNames);
    }

    private static IEnumerable<Assembly> EnumerateTrustedPlatformAssemblies()
    {
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies().Where(assembly => !assembly.IsDynamic))
        {
            if (seenNames.Add(assembly.GetName().Name ?? assembly.FullName ?? string.Empty))
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
            var assembly = TryLoadPlatformAssembly(path);

            if (assembly is null)
            {
                continue;
            }

            if (seenNames.Add(assembly.GetName().Name ?? assembly.FullName ?? string.Empty))
            {
                yield return assembly;
            }
        }
    }

    private static Assembly? TryLoadPlatformAssembly(string path)
    {
        try
        {
            var assemblyName = AssemblyName.GetAssemblyName(path);
            var loadedAssembly = AppDomain.CurrentDomain
                .GetAssemblies()
                .FirstOrDefault(candidate => AssemblyName.ReferenceMatchesDefinition(candidate.GetName(), assemblyName));

            if (loadedAssembly is not null)
            {
                return loadedAssembly;
            }

            return AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
        }
        catch
        {
            return null;
        }
    }

    private static void RegisterType(
        IDictionary<string, Type> fullNames,
        IDictionary<string, Type> simpleNames,
        Type type)
    {
        if (!string.IsNullOrWhiteSpace(type.FullName))
        {
            fullNames.TryAdd(type.FullName, type);
            fullNames.TryAdd(StripGenericArity(type.FullName), type);
        }

        simpleNames.TryAdd(type.Name, type);
        simpleNames.TryAdd(StripGenericArity(type.Name), type);
    }

    private sealed record PlatformTypeIndex(
        IReadOnlyDictionary<string, Type> FullNames,
        IReadOnlyDictionary<string, Type> SimpleNames)
    {
        public bool TryGet(string name, out Type? type)
        {
            if (name.Contains('.', StringComparison.Ordinal))
            {
                return FullNames.TryGetValue(name, out type);
            }

            return SimpleNames.TryGetValue(name, out type);
        }
    }
}
