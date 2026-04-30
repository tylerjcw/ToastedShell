namespace Tosh.Core;

public sealed class DisplayProfileRegistry
{
    private readonly Dictionary<Type, DisplayProfile> _profiles = [];
    private readonly List<DisplayProfile> _registrationOrder = [];
    private readonly Dictionary<Type, DisplayProfile?> _resolveCache = [];

    public void Register(DisplayProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        if (_profiles.ContainsKey(profile.TargetType))
        {
            throw new InvalidOperationException($"A display profile for type '{profile.TargetType.FullName}' is already registered.");
        }

        _profiles.Add(profile.TargetType, profile);
        _registrationOrder.Add(profile);
    }

    public DisplayProfile? Resolve(Type actualType)
    {
        ArgumentNullException.ThrowIfNull(actualType);

        if (_resolveCache.TryGetValue(actualType, out var cached))
        {
            return cached;
        }

        var result = ResolveCore(actualType);
        _resolveCache[actualType] = result;
        return result;
    }

    private DisplayProfile? ResolveCore(Type actualType)
    {
        if (_profiles.TryGetValue(actualType, out var exactMatch))
        {
            return exactMatch;
        }

        if (actualType.IsGenericType &&
            _profiles.TryGetValue(actualType.GetGenericTypeDefinition(), out var genericDefinitionMatch))
        {
            return genericDefinitionMatch;
        }

        return _registrationOrder
            .Where(profile => profile.AppliesTo(actualType))
            .OrderBy(profile => GetSpecificity(actualType, profile.TargetType))
            .FirstOrDefault();
    }

    public static DisplayProfileRegistry CreateDefault(DisplayPreferences? preferences = null)
    {
        ToshRuntime.EnsureStdlibLoaded();
        var registry = new DisplayProfileRegistry();
        var prefs = preferences ?? new DisplayPreferences();
        DefaultProfileRegistrar?.Invoke(registry, prefs);
        return registry;
    }

    /// <summary>
    /// Pluggable hook used by Tosh.Stdlib to register the built-in display
    /// profiles. Tosh.Core defines no profiles of its own, so this is null when
    /// only the runtime contract is loaded; Tosh.Stdlib wires it from a
    /// [ModuleInitializer].
    /// </summary>
    public static Action<DisplayProfileRegistry, DisplayPreferences>? DefaultProfileRegistrar { get; set; }

    private static int GetSpecificity(Type actualType, Type candidateType)
    {
        if (candidateType == actualType)
        {
            return 0;
        }

        if (candidateType.IsInterface)
        {
            return 10_000 + Array.IndexOf(actualType.GetInterfaces(), candidateType);
        }

        var distance = 1;

        for (var current = actualType.BaseType; current is not null; current = current.BaseType)
        {
            if (current == candidateType)
            {
                return distance;
            }

            distance++;
        }

        return int.MaxValue;
    }
}
