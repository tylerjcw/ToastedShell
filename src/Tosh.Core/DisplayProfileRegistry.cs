namespace Tosh.Core;

public sealed class DisplayProfileRegistry
{
    private readonly Dictionary<Type, DisplayProfile> _profiles = [];
    private readonly List<DisplayProfile> _registrationOrder = [];

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

        if (_profiles.TryGetValue(actualType, out var exactMatch))
        {
            return exactMatch;
        }

        return _registrationOrder
            .Where(profile => profile.AppliesTo(actualType))
            .OrderBy(profile => GetSpecificity(actualType, profile.TargetType))
            .FirstOrDefault();
    }

    public static DisplayProfileRegistry CreateDefault(DisplayPreferences? preferences = null)
    {
        var registry = new DisplayProfileRegistry();
        BuiltInDisplayProfiles.RegisterDefaults(registry, preferences ?? new DisplayPreferences());
        return registry;
    }

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
