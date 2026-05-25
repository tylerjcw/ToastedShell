using System.Reflection;
using System.Net;
using System.Numerics;
using System.Runtime.Loader;
using System.Text.RegularExpressions;

namespace Tosh.Runtime;

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
        ["complex"] = typeof(Complex),
        ["cstr"] = typeof(string),
        ["cstring"] = typeof(string),
        ["datetime"] = typeof(DateTime),
        ["dateonly"] = typeof(DateOnly),
        ["decimal"] = typeof(decimal),
        ["double"] = typeof(double),
        ["duration"] = typeof(TemporalAmount),
        ["dynamic"] = typeof(object),
        // `Error` is the recommended base class for user-defined
        // error types declared in tosh. Exposed case-insensitively
        // so `extends Error`, `error`, and `ERROR` all resolve.
        ["error"] = typeof(ToshError),
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
        ["queue"] = typeof(Queue<object?>),
        ["stack"] = typeof(Stack<object?>),
        ["linkedlist"] = typeof(LinkedList<object?>),
        ["sortedset"] = typeof(SortedSet<object?>),
        ["sorteddict"] = typeof(SortedDictionary<string, object?>),
        ["sortedmap"] = typeof(SortedDictionary<string, object?>),
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
        ["queue"] = 1,
        ["stack"] = 1,
        ["linkedlist"] = 1,
        ["sortedset"] = 1,
        ["sorteddict"] = 2,
        ["sortedmap"] = 2,
    };
    private static readonly Lazy<PlatformTypeIndex> PlatformTypes = new(BuildPlatformTypeIndex);
    // Number of assemblies present in AppDomain when the platform index was built.
    // Assemblies loaded after this count are not yet in the index and must be scanned directly.
    private static volatile int _platformIndexedAssemblyCount;
    // Names that have been confirmed not to resolve to any type. Avoids repeated failed scans.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _negativeResultCache =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly string[] DefaultImplicitUsings =
    [
        "System.Collections",
        "System.Collections.Generic",
        "System.Drawing",
        "System.IO",
        "System.Linq",
        "System.Net",
        "System.Net.Http",
        "System.Numerics",
        "System.Text",
        "System.Text.RegularExpressions",
        "System.Threading",
        "System.Threading.Tasks",
        // Pull in the tosh runtime namespace so user code can
        // reference ToshError, TextSpan, ToshDiagnostic, etc.
        // by short name without an explicit `using`.
        "Tosh.Runtime",
    ];

    private readonly Dictionary<string, string> _aliases = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _imports = new(StringComparer.OrdinalIgnoreCase);

    public DotNetTypeResolver(bool includeDefaultUsings = true)
    {
        if (includeDefaultUsings)
        {
            foreach (var ns in DefaultImplicitUsings)
            {
                _imports.Add(ns);
            }
        }
    }

    public static IReadOnlyList<string> GetDefaultImplicitUsings() => DefaultImplicitUsings;

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

    public bool RemoveUsing(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return _imports.Remove(path);
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
        // Fast negative: previously confirmed as not resolvable and no new assemblies loaded since.
        if (_negativeResultCache.ContainsKey(name) &&
            AppDomain.CurrentDomain.GetAssemblies().Length <= _platformIndexedAssemblyCount)
        {
            type = null;
            return false;
        }

        type = Type.GetType(name, throwOnError: false, ignoreCase: true);
        if (type is not null) return true;

        // Use the platform type index (O(1) dictionary lookup).
        // If the background warm-up task hasn't finished yet this blocks once until it does,
        // after which all subsequent calls are instant.  The index covers all assemblies
        // present at startup; only newly loaded ones (load-assembly) need a direct scan.
        if (PlatformTypes.Value.TryGet(name, out type)) return true;

        var allAssemblies = AppDomain.CurrentDomain.GetAssemblies();
        var indexedCount = _platformIndexedAssemblyCount;
        for (var i = indexedCount; i < allAssemblies.Length; i++)
        {
            if (allAssemblies[i].IsDynamic) continue;
            var newMatch = SafeGetTypes(allAssemblies[i]).FirstOrDefault(t =>
                TypeNameMatches(t.FullName, name) || TypeNameMatches(t.Name, name));
            if (newMatch is not null) { type = newMatch; return true; }
        }

        // Attempt to resolve a dotted name as a nested CLR type:
        //   "Foo.Bar" → find type "Foo", then get its nested type "Bar".
        // This handles compiled tosh module shells, where nested modules
        // become nested CLR types ("Foo+Bar" in CLR notation) rather than
        // types with a dotted full name.
        {
            var dotIdx = name.LastIndexOf('.');
            if (dotIdx > 0)
            {
                var parentName = name[..dotIdx];
                var nestedName = name[(dotIdx + 1)..];
                if (TryResolveDirect(parentName, out var parentType) && parentType is not null)
                {
                    var nested = parentType.GetNestedType(
                        nestedName,
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
                    if (nested is not null) { type = nested; return true; }
                }
            }
        }

        _negativeResultCache.TryAdd(name, true);
        type = null;
        return false;
    }

    private static bool TryResolveDirectGenericDefinition(string name, int arity, out Type? type)
    {
        var cacheKey = $"{name}`{arity}";

        // Fast negative: previously confirmed as not resolvable and no new assemblies loaded since.
        if (_negativeResultCache.ContainsKey(cacheKey) &&
            AppDomain.CurrentDomain.GetAssemblies().Length <= _platformIndexedAssemblyCount)
        {
            type = null;
            return false;
        }

        type = Type.GetType(cacheKey, throwOnError: false, ignoreCase: true);
        if (type is not null) return true;

        if (PlatformTypes.Value.TryGetGenericDefinition(name, arity, out type)) return true;

        var allAssemblies = AppDomain.CurrentDomain.GetAssemblies();
        var indexedCount = _platformIndexedAssemblyCount;
        for (var i = indexedCount; i < allAssemblies.Length; i++)
        {
            if (allAssemblies[i].IsDynamic) continue;
            var newMatch = SafeGetTypes(allAssemblies[i]).FirstOrDefault(candidate =>
                candidate.IsGenericTypeDefinition &&
                candidate.GetGenericArguments().Length == arity &&
                (TypeNameMatches(candidate.FullName, name) || TypeNameMatches(candidate.Name, name)));
            if (newMatch is not null) { type = newMatch; return true; }
        }

        _negativeResultCache.TryAdd(cacheKey, true);
        type = null;
        return false;
    }

    /// <summary>
    /// Eagerly builds the platform type index in the calling thread.
    /// Call this early (e.g., from a background task at startup) so that
    /// subsequent type resolution calls are O(1) dictionary lookups.
    /// </summary>
    public static void WarmUpPlatformTypeIndex()
    {
        _ = PlatformTypes.Value;
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
            "queue" => typeof(Queue<>).MakeGenericType(arguments[0]),
            "stack" => typeof(Stack<>).MakeGenericType(arguments[0]),
            "linkedlist" => typeof(LinkedList<>).MakeGenericType(arguments[0]),
            "sortedset" => typeof(SortedSet<>).MakeGenericType(arguments[0]),
            "sorteddict" or "sortedmap" => typeof(SortedDictionary<,>).MakeGenericType(arguments[0], arguments[1]),
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

        // Snapshot the count BEFORE iteration. Any assembly loaded during
        // (or after) indexing must be rescannable by TryResolveDirect's
        // fallback loop. Capturing after iteration creates a race where
        // assemblies loaded mid-build are neither in the index nor in
        // the rescan range — biting hard in single-file publishes where
        // System.Drawing.Primitives & friends load lazily as types
        // referenced by DisplayEngine are touched.
        var indexedCount = AppDomain.CurrentDomain.GetAssemblies().Length;

        foreach (var assembly in EnumerateTrustedPlatformAssemblies())
        {
            foreach (var type in SafeGetTypes(assembly))
            {
                RegisterType(fullNames, simpleNames, type);
            }
        }

        var index = new PlatformTypeIndex(
            fullNames,
            simpleNames,
            fullNames.Values
                .Concat(simpleNames.Values)
                .Distinct()
                .ToArray());

        _platformIndexedAssemblyCount = indexedCount;

        return index;
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
