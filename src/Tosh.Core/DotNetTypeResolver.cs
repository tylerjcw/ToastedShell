using System.Reflection;
using System.Net;
using System.Numerics;
using System.Runtime.Loader;
using System.Text.RegularExpressions;

namespace Tosh.Core;

public sealed class DotNetTypeResolver : IImportingTypeResolver
{
    private static readonly IReadOnlyDictionary<string, Type> Aliases = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase)
    {
        ["bool"] = typeof(bool),
        ["bigint"] = typeof(BigInteger),
        ["biginteger"] = typeof(BigInteger),
        ["byte"] = typeof(byte),
        ["sbyte"] = typeof(sbyte),
        ["char"] = typeof(char),
        ["cstr"] = typeof(string),
        ["cstring"] = typeof(string),
        ["datetime"] = typeof(DateTime),
        ["dateonly"] = typeof(DateOnly),
        ["decimal"] = typeof(decimal),
        ["double"] = typeof(double),
        ["duration"] = typeof(TemporalAmount),
        ["dynamic"] = typeof(object),
        ["table"] = typeof(System.Dynamic.ExpandoObject),
        ["dict"] = typeof(Dictionary<string, object?>),
        ["map"] = typeof(Dictionary<string, object?>),
        ["file"] = typeof(FileInfo),
        ["float"] = typeof(float),
        ["guid"] = typeof(Guid),
        ["ip"] = typeof(IPAddress),
        ["ipaddress"] = typeof(IPAddress),
        ["int"] = typeof(int),
        ["uint"] = typeof(uint),
        ["intptr"] = typeof(IntPtr),
        ["list"] = typeof(List<object?>),
        ["long"] = typeof(long),
        ["ulong"] = typeof(ulong),
        ["nint"] = typeof(IntPtr),
        ["nuint"] = typeof(UIntPtr),
        ["object"] = typeof(object),
        ["array"] = typeof(object[]),
        ["regex"] = typeof(Regex),
        ["short"] = typeof(short),
        ["ushort"] = typeof(ushort),
        ["string"] = typeof(string),
        ["set"] = typeof(HashSet<object?>),
        ["hashtable"] = typeof(System.Collections.Hashtable),
        ["temporalamount"] = typeof(TemporalAmount),
        ["timeonly"] = typeof(TimeOnly),
        ["timespan"] = typeof(TimeSpan),
        ["tuple"] = typeof(ToshTuple),
        ["uri"] = typeof(Uri),
        ["ptr"] = typeof(IntPtr),
        ["uptr"] = typeof(UIntPtr),
        ["uintptr"] = typeof(UIntPtr),
    };
    private static readonly IReadOnlyDictionary<string, int> GenericAliasArities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        ["list"] = 1,
        ["array"] = 1,
        ["dict"] = 2,
        ["map"] = 2,
        ["set"] = 1,
    };
    private static readonly Lazy<PlatformTypeIndex> PlatformTypes = new(BuildPlatformTypeIndex);

    private readonly Dictionary<string, string> _aliases = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _imports = new(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyDictionary<string, Type> BuiltInAliases => Aliases;

    public static IReadOnlyCollection<Type> GetKnownTypes() => PlatformTypes.Value.Types;

    public IReadOnlyCollection<string> GetImports() => _imports.ToArray();

    public IReadOnlyDictionary<string, string> GetAliases() =>
        new Dictionary<string, string>(_aliases, StringComparer.OrdinalIgnoreCase);

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

        if (TryResolveConstructedType(name, out var constructed))
        {
            return constructed;
        }

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

    private bool TryResolveConstructedType(string name, out Type? type)
    {
        if (!TryParseConstructedTypeName(name, out var baseName, out var argumentNames))
        {
            type = null;
            return false;
        }

        var resolvedArguments = new Type[argumentNames.Count];

        for (var index = 0; index < argumentNames.Count; index++)
        {
            var resolvedArgument = Resolve(argumentNames[index]);

            if (resolvedArgument is null)
            {
                type = null;
                return false;
            }

            resolvedArguments[index] = resolvedArgument;
        }

        if (TryResolveGenericAlias(baseName, resolvedArguments, out type))
        {
            return true;
        }

        if (TryResolveGenericDefinition(baseName, resolvedArguments.Length, out var definition))
        {
            type = definition!.MakeGenericType(resolvedArguments);
            return true;
        }

        type = null;
        return false;
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

    private bool TryResolveGenericDefinition(string name, int arity, out Type? type)
    {
        if (TryResolveAliasedPath(name, out var aliasedPath) &&
            TryResolveDirectGenericDefinition(aliasedPath, arity, out type))
        {
            return true;
        }

        if (TryResolveDirectGenericDefinition(name, arity, out type))
        {
            return true;
        }

        foreach (var importPath in _imports)
        {
            if (string.Equals(GetLastSegment(importPath), name, StringComparison.OrdinalIgnoreCase) &&
                TryResolveDirectGenericDefinition(importPath, arity, out type))
            {
                return true;
            }

            if (TryResolveDirectGenericDefinition(importPath + "." + name, arity, out type))
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

    private static bool TryResolveDirectGenericDefinition(string name, int arity, out Type? type)
    {
        type = Type.GetType($"{name}`{arity}", throwOnError: false, ignoreCase: true);

        if (type is not null)
        {
            return true;
        }

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies().Where(assembly => !assembly.IsDynamic))
        {
            var match = SafeGetTypes(assembly).FirstOrDefault(candidate =>
                candidate.IsGenericTypeDefinition &&
                candidate.GetGenericArguments().Length == arity &&
                (TypeNameMatches(candidate.FullName, name) || TypeNameMatches(candidate.Name, name)));

            if (match is not null)
            {
                type = match;
                return true;
            }
        }

        if (PlatformTypes.Value.TryGetGenericDefinition(name, arity, out type))
        {
            return true;
        }

        type = null;
        return false;
    }

    private static bool TryResolveGenericAlias(string name, IReadOnlyList<Type> arguments, out Type? type)
    {
        if (!GenericAliasArities.TryGetValue(name, out var arity) || arity != arguments.Count)
        {
            type = null;
            return false;
        }

        type = name.ToLowerInvariant() switch
        {
            "list" => typeof(List<>).MakeGenericType(arguments[0]),
            "array" => arguments[0].MakeArrayType(),
            "dict" or "map" => typeof(Dictionary<,>).MakeGenericType(arguments[0], arguments[1]),
            "set" => typeof(HashSet<>).MakeGenericType(arguments[0]),
            _ => null,
        };

        return type is not null;
    }

    private static bool TryParseConstructedTypeName(string name, out string baseName, out IReadOnlyList<string> argumentNames)
    {
        baseName = string.Empty;
        argumentNames = Array.Empty<string>();

        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var trimmed = name.Trim();
        var firstOpen = trimmed.IndexOf('<');

        if (firstOpen < 0 || !trimmed.EndsWith('>'))
        {
            return false;
        }

        var depth = 0;
        var closeIndex = -1;

        for (var index = firstOpen; index < trimmed.Length; index++)
        {
            switch (trimmed[index])
            {
                case '<':
                    depth++;
                    break;
                case '>':
                    depth--;
                    if (depth == 0)
                    {
                        closeIndex = index;
                    }
                    break;
            }

            if (depth < 0)
            {
                return false;
            }
        }

        if (depth != 0 || closeIndex != trimmed.Length - 1)
        {
            return false;
        }

        baseName = trimmed[..firstOpen].Trim();

        if (string.IsNullOrWhiteSpace(baseName))
        {
            return false;
        }

        argumentNames = SplitTypeArguments(trimmed[(firstOpen + 1)..closeIndex]);
        return argumentNames.Count > 0;
    }

    private static IReadOnlyList<string> SplitTypeArguments(string argumentText)
    {
        var arguments = new List<string>();
        var depth = 0;
        var start = 0;

        for (var index = 0; index < argumentText.Length; index++)
        {
            switch (argumentText[index])
            {
                case '<':
                    depth++;
                    break;
                case '>':
                    depth--;
                    break;
                case ',' when depth == 0:
                    arguments.Add(argumentText[start..index].Trim());
                    start = index + 1;
                    break;
            }
        }

        var trailing = argumentText[start..].Trim();

        if (!string.IsNullOrWhiteSpace(trailing))
        {
            arguments.Add(trailing);
        }

        return arguments;
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

        return new PlatformTypeIndex(
            fullNames,
            simpleNames,
            fullNames.Values
                .Concat(simpleNames.Values)
                .Distinct()
                .ToArray());
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
        IReadOnlyDictionary<string, Type> SimpleNames,
        IReadOnlyCollection<Type> Types)
    {
        public bool TryGet(string name, out Type? type)
        {
            if (name.Contains('.', StringComparison.Ordinal))
            {
                return FullNames.TryGetValue(name, out type);
            }

            return SimpleNames.TryGetValue(name, out type);
        }

        public bool TryGetGenericDefinition(string name, int arity, out Type? type)
        {
            type = Types.FirstOrDefault(candidate =>
                candidate.IsGenericTypeDefinition &&
                candidate.GetGenericArguments().Length == arity &&
                (TypeNameMatches(candidate.FullName, name) || TypeNameMatches(candidate.Name, name)));

            return type is not null;
        }
    }
}
