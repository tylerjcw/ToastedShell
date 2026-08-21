using System.Reflection;
using System.Net;
using System.Numerics;
using System.Runtime.Loader;
using System.Text.RegularExpressions;
using Tosh.Runtime.Units;

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
        ["quantity"] = typeof(Quantity),
        ["length"] = typeof(LengthQuantity),
        ["lengthquantity"] = typeof(LengthQuantity),
        ["mass"] = typeof(MassQuantity),
        ["massquantity"] = typeof(MassQuantity),
        ["durationquantity"] = typeof(DurationQuantity),
        ["timequantity"] = typeof(DurationQuantity),
        ["temperature"] = typeof(TemperatureQuantity),
        ["temperaturequantity"] = typeof(TemperatureQuantity),
        ["datasize"] = typeof(DataSizeQuantity),
        ["datasizequantity"] = typeof(DataSizeQuantity),
        ["storagesize"] = typeof(StorageSize),
        ["speed"] = typeof(SpeedQuantity),
        ["speedquantity"] = typeof(SpeedQuantity),
        ["area"] = typeof(AreaQuantity),
        ["areaquantity"] = typeof(AreaQuantity),
        ["volume"] = typeof(VolumeQuantity),
        ["volumequantity"] = typeof(VolumeQuantity),
        ["force"] = typeof(ForceQuantity),
        ["forcequantity"] = typeof(ForceQuantity),
        ["energy"] = typeof(EnergyQuantity),
        ["energyquantity"] = typeof(EnergyQuantity),
        ["power"] = typeof(PowerQuantity),
        ["powerquantity"] = typeof(PowerQuantity),
        ["pressure"] = typeof(PressureQuantity),
        ["pressurequantity"] = typeof(PressureQuantity),
        ["frequency"] = typeof(FrequencyQuantity),
        ["frequencyquantity"] = typeof(FrequencyQuantity),
        ["angle"] = typeof(AngleQuantity),
        ["anglequantity"] = typeof(AngleQuantity),
        ["acceleration"] = typeof(AccelerationQuantity),
        ["accelerationquantity"] = typeof(AccelerationQuantity),
        ["density"] = typeof(DensityQuantity),
        ["densityquantity"] = typeof(DensityQuantity),
        ["voltage"] = typeof(VoltageQuantity),
        ["voltagequantity"] = typeof(VoltageQuantity),
        ["current"] = typeof(CurrentQuantity),
        ["currentquantity"] = typeof(CurrentQuantity),
        ["resistance"] = typeof(ResistanceQuantity),
        ["resistancequantity"] = typeof(ResistanceQuantity),
        ["charge"] = typeof(ChargeQuantity),
        ["chargequantity"] = typeof(ChargeQuantity),
        ["torque"] = typeof(TorqueQuantity),
        ["torquequantity"] = typeof(TorqueQuantity),
        ["flowrate"] = typeof(FlowRateQuantity),
        ["flowratequantity"] = typeof(FlowRateQuantity),
        ["capacitance"] = typeof(CapacitanceQuantity),
        ["capacitancequantity"] = typeof(CapacitanceQuantity),
        ["inductance"] = typeof(InductanceQuantity),
        ["inductancequantity"] = typeof(InductanceQuantity),
        ["substance"] = typeof(SubstanceQuantity),
        ["substancequantity"] = typeof(SubstanceQuantity),
        ["luminosity"] = typeof(LuminosityQuantity),
        ["luminosityquantity"] = typeof(LuminosityQuantity),
        ["angularvelocity"] = typeof(AngularVelocityQuantity),
        ["angularvelocityquantity"] = typeof(AngularVelocityQuantity),
        ["dynamic"] = typeof(object),
        // `Error` is the recommended base class for user-defined
        // error types declared in tosh. Exposed case-insensitively
        // so `extends Error`, `error`, and `ERROR` all resolve.
        ["error"] = typeof(ToshError),
        // `TOAST-0031`. `Failure` is anything the language raised — a declared error or a
        // diagnostic — so a handler can catch broadly without naming a CLR type.
        // `Diagnostic` is the language reporting that an operation had no answer, which
        // had no Tōast name at all: `$e is Exception` was the only way to ask, and that is
        // a word a target without the CLR does not have.
        ["failure"] = typeof(IToshFailure),
        ["diagnostic"] = typeof(ToshDiagnosticException),
        // Raised when a native call fails its declared success contract, so
        // `catch (e) { $e is NativeError }` and `extends NativeError` both work.
        ["nativeerror"] = typeof(NativeError),
        // The anonymous dynamic record. `record` is the name the syntax and
        // the specification use; `table` stays resolvable as an alias (TS-P3-11).
        ["record"] = typeof(System.Dynamic.ExpandoObject),
        ["table"] = typeof(System.Dynamic.ExpandoObject),
        ["dynamicrecord"] = typeof(System.Dynamic.ExpandoObject),
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
    // Names confirmed not to resolve, mapped to the loaded-assembly count at the
    // moment that was established. A miss is only re-scanned when an assembly has
    // actually appeared since — previously the guard compared against
    // `_platformIndexedAssemblyCount`, which is a pre-index snapshot that never
    // advances, so the negative cache switched itself off the first time anything
    // loaded and every subsequent miss re-enumerated `Assembly.GetTypes()` over
    // every assembly beyond the watermark. Module-local names (a `raw struct`, a
    // class declared in the same file) never resolve as CLR types, so every
    // annotation mentioning one paid that scan.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _negativeResultCache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// How many assemblies are loaded, maintained by the runtime's own event rather
    /// than counted on demand.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The cache-invalidation guards below only need to know whether the set of loaded
    /// assemblies has *changed* since they last looked, and they were answering that with
    /// <c>AppDomain.CurrentDomain.GetAssemblies().Length</c> — a call that takes a runtime
    /// lock and allocates an array of every loaded assembly, purely to read its length.
    /// On the fast path — a cache *hit* — that was the entire cost of the call.
    /// </para>
    /// <para>
    /// It showed up as `pthread_mutex_lock` and `__tls_get_addr` dominating a profile of
    /// `var x: int = 0` counting to a million: an annotated assignment resolves its type
    /// name twice, so a tight loop took the runtime's assembly lock two million times.
    /// Removing it made that loop about 3x faster (`TS-P2-119`).
    /// </para>
    /// <para>
    /// The count is a generation number, not a population: it only has to differ when
    /// something new has loaded. Assemblies are never unloaded here, and the snapshot is
    /// taken before the event is attached, so nothing is missed.
    /// </para>
    /// </remarks>
    private static int _loadedAssemblyCount;

    static DotNetTypeResolver()
    {
        AppDomain.CurrentDomain.AssemblyLoad += static (_, _) =>
            Interlocked.Increment(ref _loadedAssemblyCount);

        _loadedAssemblyCount = AppDomain.CurrentDomain.GetAssemblies().Length;
    }

    /// <summary>The generation the caches compare against; see <see cref="_loadedAssemblyCount"/>.</summary>
    private static int LoadedAssemblyCount => Volatile.Read(ref _loadedAssemblyCount);

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
        "Tosh.Runtime.Units",
    ];

    private readonly Dictionary<string, string> _aliases = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _imports = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Names already resolved by this instance, successes and failures alike
    /// (<c>TS-P1-42</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// There was a <see cref="_negativeResultCache"/> and nothing for successes, so every
    /// hit repeated the whole search. Measured, a static call cost ~170ms —
    /// <c>Path.GetRelativePath</c> 50 times took 8.3 seconds against 2ms for 50 instance
    /// calls, a ~4,000x gap — because resolving one unqualified name walks a dozen
    /// imports, and each miss falls into <c>TryResolveDirect</c>, whose nested-type
    /// fallback then <em>recurses</em> on the parent name:
    /// <c>System.Collections.Generic.Path</c> → <c>System.Collections.Generic</c> →
    /// <c>System.Collections</c> → <c>System</c>, calling
    /// <c>AppDomain.CurrentDomain.GetAssemblies()</c> twice at every level.
    /// </para>
    /// <para>
    /// Per-instance, not static, because the answer depends on <see cref="_imports"/> and
    /// <see cref="_aliases"/> — two resolvers with different <c>using</c> sets legitimately
    /// resolve the same name to different types, which is the whole point of
    /// <c>TS-P2-66</c>. Mutating either clears it.
    /// </para>
    /// <para>
    /// Concurrent because the engine resolves from async continuations, and keyed
    /// case-insensitively to match every other lookup here.
    /// </para>
    /// </remarks>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Type?> _resolutionCache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Answers already given by <see cref="ResolveAliasCaseVariant"/> — <c>TS-P2-37</c>.
    /// </summary>
    /// <remarks>
    /// Keyed <see cref="StringComparer.Ordinal"/>, unlike every other cache here, because the
    /// question this one answers *is* the casing: <c>File</c> and <c>file</c> must not share an
    /// entry. Without it the check ran the full uncached search on every dotted call and cost
    /// what <c>TS-P1-42</c> measured and removed — 3,000 <c>File.Exists</c> calls took 18.1s
    /// against 0.41s for an unaffected name, a 44x gap, which is how it was caught.
    /// </remarks>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Type?> _aliasCaseVariantCache =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Assembly count when <see cref="_resolutionCache"/> was last valid. A newly loaded
    /// assembly can turn a cached failure into a success, and can shadow a cached success
    /// with a nearer match — <c>TS-P2-48</c> showed emitted assemblies really do appear
    /// mid-run — so any change in the count drops the cache wholesale. Same guard the
    /// negative cache already uses, rather than a second theory of invalidation.
    /// </summary>
    private int _resolutionCacheAssemblyCount = -1;

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

    /// <summary>
    /// Resolves a type name against the platform index — `TOAST-0029`.
    /// </summary>
    /// <remarks>
    /// Exposed so `is` can answer a bare name. It had only `Type.GetType`, which needs an
    /// assembly qualifier, so `$e is Exception` and `[1,2] is IEnumerable` were always
    /// false while the fully-qualified spellings of both were true. The index is the same
    /// one `Resolve` consults, so the operator and an import cannot come to disagree about
    /// what a name means.
    /// </remarks>
    public static bool TryResolveKnownType(string name, out Type? type) =>
        PlatformTypes.Value.TryGet(name, out type);

    /// <summary>
    /// Resolves a type name the way Tōast means it — `TOAST-0030`.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The language's own names come first, then the platform index. Both are needed and
    /// the order matters: `Error` is a Tōast name for <see cref="ToshError"/> and resolves
    /// to nothing at all in the CLR, while `Exception` is a CLR name the index answers.
    /// </para>
    /// <para>
    /// This exists because the two backends had drifted about which names are real. The
    /// compiled `new` consulted user-declared types and then <c>Type.GetType</c>, which
    /// needs an assembly qualifier, so <c>new Error("x")</c> failed with "unknown type
    /// 'Error'" while the interpreter built one. That single failure produced two of the
    /// recorded divergences, because <c>try { throw new Error("x") } catch (e) { $e is
    /// Error }</c> then caught the *resolution failure* and answered false about an
    /// <see cref="InvalidOperationException"/>.
    /// </para>
    /// <para>
    /// It lives here, in the portable runtime, rather than on the engine: a backend that
    /// asked the interpreter what a name means would be depending on the interpreter,
    /// which is what Phase B exists to remove.
    /// </para>
    /// </remarks>
    public static bool TryResolveToastTypeName(string name, out Type? type)
    {
        if (!string.IsNullOrWhiteSpace(name) && Aliases.TryGetValue(name, out var aliased))
        {
            type = aliased;
            return true;
        }

        return TryResolveKnownType(name, out type);
    }

    public IReadOnlyCollection<string> GetImports() => _imports.ToArray();

    public IReadOnlyDictionary<string, string> GetAliases() =>
        new Dictionary<string, string>(_aliases, StringComparer.OrdinalIgnoreCase);

    public void AddUsing(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _imports.Add(path);
        _resolutionCache.Clear();
        _aliasCaseVariantCache.Clear();
    }

    public bool RemoveUsing(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var removed = _imports.Remove(path);
        if (removed) { _resolutionCache.Clear(); _aliasCaseVariantCache.Clear(); }
        return removed;
    }

    public void AddAlias(string alias, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(alias);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _aliases[alias] = path;
        _resolutionCache.Clear();
        _aliasCaseVariantCache.Clear();
    }

    public Type? Resolve(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        // `TS-P1-42`. The search below is expensive enough that repeating it per call made
        // static member access unusable from script; see `_resolutionCache`.
        var assemblyCount = LoadedAssemblyCount;

        if (assemblyCount != _resolutionCacheAssemblyCount)
        {
            // Only *negative* results can be invalidated by a newly loaded
            // assembly: loading one can make a name resolvable, never make a
            // resolved name stop resolving. Clearing the positives too meant the
            // whole cache was discarded every time an assembly appeared — and
            // loading a module is itself something that loads assemblies, so
            // during startup the cache was wiped repeatedly at exactly the
            // moment it was most needed.
            //
            // Measured before this: type-name resolution was ~518 ms of the
            // ~574 ms spent loading a profile's modules, 485 ms of it inside
            // TryResolveFromImports, because almost every lookup missed.
            foreach (var entry in _resolutionCache)
            {
                if (entry.Value is null)
                {
                    _resolutionCache.TryRemove(entry.Key, out _);
                }
            }

            _aliasCaseVariantCache.Clear();
            _resolutionCacheAssemblyCount = assemblyCount;
        }

        if (_resolutionCache.TryGetValue(name, out var cached))
        {
            return cached;
        }

        var resolved = ResolveUncached(name);
        _resolutionCache[name] = resolved;
        return resolved;
    }

    /// <summary>
    /// The CLR type a name asks for when it is spelled differently from the shell alias it
    /// collides with — <c>TS-P2-37</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The alias table is matched case-insensitively, so <c>File</c> found <c>file</c> and
    /// resolved to <see cref="FileInfo"/>; every static on <c>System.IO.File</c> then failed with
    /// a message naming <c>FileInfo</c>, a type the reader never wrote. The same collision hits
    /// <c>Array</c> (alias <c>array</c>, an <c>object[]</c>) and <c>Tuple</c>.
    /// </para>
    /// <para>
    /// The aliases are the shell's own vocabulary and their canonical spelling is lower-case, so a
    /// capitalised spelling is a CLR type name and is answered as one — but only where a type is
    /// being *used* as a type, which is static member access. <c>Resolve</c> is unchanged, so
    /// <c>var f: file</c> and <c>var f: File</c> both still bind <see cref="FileInfo"/> and
    /// <c>var q: Queue</c> does not silently become <c>System.Collections.Queue</c>.
    /// </para>
    /// <para>
    /// Returns <see langword="null"/> when the name matches an alias exactly, when it matches none,
    /// or when no CLR type of that name resolves — in each case the ordinary lookup is right.
    /// </para>
    /// </remarks>
    public Type? ResolveAliasCaseVariant(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        if (!Aliases.TryGetValue(name, out var alias)) return null;

        // Written exactly as the shell spells it: the alias is what was asked for.
        if (IsCanonicalAliasSpelling(name)) return null;

        // Same invalidation as `Resolve`: a newly loaded assembly can turn a failure into a
        // success, so any change in the count drops the cache wholesale.
        var assemblyCount = LoadedAssemblyCount;

        if (assemblyCount != _resolutionCacheAssemblyCount)
        {
            _resolutionCache.Clear();
            _aliasCaseVariantCache.Clear();
            _resolutionCacheAssemblyCount = assemblyCount;
        }
        else if (_aliasCaseVariantCache.TryGetValue(name, out var cached))
        {
            return cached;
        }

        var direct = ResolveWithoutAliases(name);
        var resolved = direct is null || direct == alias ? null : direct;
        _aliasCaseVariantCache[name] = resolved;
        return resolved;
    }

    /// <summary>True when <paramref name="name"/> is spelled as the alias table declares it.</summary>
    /// <remarks>
    /// The table's keys are the canonical spelling, and every one of them is lower-case. Comparing
    /// against the stored key rather than testing for lower-case keeps that an observation about
    /// the table instead of an assumption about it.
    /// </remarks>
    private static bool IsCanonicalAliasSpelling(string name)
    {
        foreach (var key in Aliases.Keys)
        {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
            {
                return string.Equals(key, name, StringComparison.Ordinal);
            }
        }

        return false;
    }

    /// <summary>The ordinary lookup with the alias table taken out of it.</summary>
    private Type? ResolveWithoutAliases(string name)
    {
        if (TryResolveAliasedPath(name, out var aliasedPath) &&
            TryResolveDirect(aliasedPath, out var aliasedType))
        {
            return aliasedType;
        }

        var isUnqualified = !name.Contains('.', StringComparison.Ordinal);

        if (isUnqualified && TryResolveFromImports(name, out var importedFirst))
        {
            return importedFirst;
        }

        if (TryResolveDirect(name, out var direct))
        {
            return direct;
        }

        return !isUnqualified && TryResolveFromImports(name, out var imported) ? imported : null;
    }

    private Type? ResolveUncached(string name)
    {
        // `TS-P2-69`. A postfix `[]` is an array of the element type, resolved by taking
        // the suffix off and asking for the element — so `string[]` follows every rule
        // `string` does, including the import precedence from `TS-P2-66`, rather than
        // needing its own lookup table. Repeats for jagged arrays.
        if (name.EndsWith("[]", StringComparison.Ordinal))
        {
            var elementName = name[..^2];

            return string.IsNullOrWhiteSpace(elementName)
                ? null
                : Resolve(elementName)?.MakeArrayType();
        }

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

        // `TS-P2-66`. An explicit `using` outranks an incidental match, for an *unqualified* name.
        //
        // The unqualified scan searches the platform index and every loaded assembly, including
        // private nested implementation types, and it used to run first — so a stated intention
        // lost to whatever the runtime happened to hold. Measured across the 16,727 simple names
        // the platform index knows: 33 resolve differently under the two orders, and in every one
        // the import is the type a reader means. `Complex` resolved to
        // `System.Threading.PortableThreadPool+HillClimbing+Complex` rather than
        // `System.Numerics.Complex`; `BigInteger` to `System.Number+BigInteger`; `SpinLock` to a
        // nested field of `ReaderWriterLockSlim`. Nothing is lost by the reorder — no name
        // resolves through imports alone that direct would have found, so the fallthrough below
        // still answers the other 15,544.
        //
        // A *qualified* name is already an instruction about where to look, so it keeps direct
        // resolution first.
        var isUnqualified = !name.Contains('.', StringComparison.Ordinal);

        if (isUnqualified && TryResolveFromImports(name, out var importedFirst))
        {
            return importedFirst;
        }

        if (TryResolveDirect(name, out var direct))
        {
            return direct;
        }

        if (!isUnqualified && TryResolveFromImports(name, out var importedType))
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

        // The same precedence as `Resolve`, for the generic form: `using` beats an incidental
        // match on an unqualified name (`TS-P2-66`).
        if (!name.Contains('.', StringComparison.Ordinal))
        {
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
        if (_negativeResultCache.TryGetValue(name, out var negativeAtCount) &&
            LoadedAssemblyCount <= negativeAtCount)
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

        _negativeResultCache[name] = LoadedAssemblyCount;
        type = null;
        return false;
    }

    private static bool TryResolveDirectGenericDefinition(string name, int arity, out Type? type)
    {
        var cacheKey = $"{name}`{arity}";

        // Fast negative: previously confirmed as not resolvable and no new assemblies loaded since.
        if (_negativeResultCache.TryGetValue(cacheKey, out var negativeGenericAtCount) &&
            LoadedAssemblyCount <= negativeGenericAtCount)
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

        _negativeResultCache[cacheKey] = LoadedAssemblyCount;
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
        var indexedCount = LoadedAssemblyCount;

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
