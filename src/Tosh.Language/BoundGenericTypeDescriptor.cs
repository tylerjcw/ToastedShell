using Tosh.Runtime;

namespace Tosh.Language;

/// <summary>
/// An <see cref="IShellTypeDescriptor"/> view of a generic
/// <see cref="ToshClassDefinition"/> closed over a concrete set of
/// type arguments (e.g. <c>Point2D&lt;int&gt;</c>).
/// </summary>
/// <remarks>
/// The wrapper delegates every member to the underlying open
/// definition except <see cref="ShellTypeName"/> /
/// <see cref="ShellFullName"/>, which render the constructed form
/// (<c>Point2D&lt;int&gt;</c>) so commands like <c>type-of</c> can
/// surface the bound argument list to the user.
/// </remarks>
internal sealed class BoundGenericTypeDescriptor : IShellTypeDescriptor
{
    private readonly ToshClassDefinition _definition;
    private readonly IReadOnlyList<Type?> _orderedArguments;
    private readonly string _displayName;

    public BoundGenericTypeDescriptor(
        ToshClassDefinition definition,
        IReadOnlyDictionary<string, Type?> bindings)
    {
        _definition = definition;
        var names = definition.TypeParameterNames;
        var ordered = new Type?[names.Count];
        for (var i = 0; i < names.Count; i++)
        {
            ordered[i] = bindings.TryGetValue(names[i], out var t) ? t : null;
        }
        _orderedArguments = ordered;

        var args = new string[ordered.Length];
        for (var i = 0; i < ordered.Length; i++)
        {
            args[i] = ordered[i]?.Name ?? names[i];
        }
        _displayName = $"{definition.Name}<{string.Join(", ", args)}>";
    }

    /// <summary>The constructed type-argument list, in declaration order.</summary>
    public IReadOnlyList<Type?> TypeArguments => _orderedArguments;

    /// <summary>The underlying open generic definition.</summary>
    public ToshClassDefinition Definition => _definition;

    public string ShellTypeName => _displayName;
    public string ShellFullName => _displayName;
    public string? ShellNamespace => _definition.ShellNamespace;
    public string? ShellAssemblyName => _definition.ShellAssemblyName;
    public string? ShellBaseTypeName => _definition.ShellBaseTypeName;
    public bool ShellIsClass => _definition.ShellIsClass;
    public bool ShellIsInterface => _definition.ShellIsInterface;
    public bool ShellIsEnum => _definition.ShellIsEnum;
    public bool ShellIsValueType => _definition.ShellIsValueType;
    public bool ShellIsAbstract => _definition.ShellIsAbstract;
    public bool ShellIsGenericType => true;
    public bool ShellIsArray => _definition.ShellIsArray;
    public bool ShellIsPublic => _definition.ShellIsPublic;

    public IReadOnlyList<ShellMemberDescriptor> GetShellMembers(bool includeHidden = false)
        => _definition.GetShellMembers(includeHidden);

    public IReadOnlyList<ShellMethodDescriptor> GetShellMethods(bool includeHidden = false)
        => _definition.GetShellMethods(includeHidden);

    public IReadOnlyList<ShellConstructorDescriptor> GetShellConstructors()
        => _definition.GetShellConstructors();

    public bool TryGetMember(string name, out object? value, bool includeHidden = false)
    {
        if (string.Equals(name, "TypeArguments", StringComparison.OrdinalIgnoreCase))
        {
            value = _orderedArguments;
            return true;
        }
        if (string.Equals(name, "Name", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "FullName", StringComparison.OrdinalIgnoreCase))
        {
            value = _displayName;
            return true;
        }
        if (string.Equals(name, "IsGenericType", StringComparison.OrdinalIgnoreCase))
        {
            value = true;
            return true;
        }
        return _definition.TryGetMember(name, out value, includeHidden);
    }

    public bool TrySetMember(string name, object? value) => false;

    public IReadOnlyList<KeyValuePair<string, object?>> GetMembers(bool includeHidden = false)
    {
        var inner = _definition.GetMembers(includeHidden);
        var list = new List<KeyValuePair<string, object?>>(inner.Count + 1)
        {
            new("Name", ShellTypeName),
            new("FullName", ShellFullName),
        };
        foreach (var pair in inner)
        {
            if (string.Equals(pair.Key, "Name", StringComparison.Ordinal) ||
                string.Equals(pair.Key, "FullName", StringComparison.Ordinal))
            {
                continue;
            }
            list.Add(pair);
        }
        list.Add(new KeyValuePair<string, object?>("TypeArguments", _orderedArguments));
        return list;
    }

    public override string ToString() => _displayName;
}
