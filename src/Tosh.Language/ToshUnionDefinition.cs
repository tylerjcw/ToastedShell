using Tosh.Core;
using Tosh.Language.Parsing;

namespace Tosh.Language;

public sealed class ToshUnionDefinition : IShellNamedType
{
    private readonly Dictionary<string, UnionVariantDefinition> _variantsByName;

    public ToshUnionDefinition(
        string name,
        IReadOnlyList<UnionVariantDefinition> variants,
        string sourceName,
        string sourceText,
        TextSpan span)
    {
        Name = name;
        Variants = variants;
        SourceName = sourceName;
        SourceText = sourceText;
        Span = span;
        _variantsByName = variants.ToDictionary(v => v.Name, StringComparer.OrdinalIgnoreCase);
    }

    public string Name { get; }

    public IReadOnlyList<UnionVariantDefinition> Variants { get; }

    public string SourceName { get; }

    public string SourceText { get; }

    public TextSpan Span { get; }

    public string ShellTypeName => Name;

    public string ShellFullName => Name;

    public string? ShellNamespace => null;

    public string? ShellAssemblyName => "ToSh";

    public string? ShellBaseTypeName => null;

    public bool ShellIsClass => false;

    public bool ShellIsInterface => false;

    public bool ShellIsEnum => false;

    public bool ShellIsValueType => false;

    public bool ShellIsAbstract => false;

    public bool ShellIsGenericType => false;

    public bool ShellIsArray => false;

    public bool ShellIsPublic => true;

    public object CreateInstance(IReadOnlyList<object?> arguments)
    {
        throw new InvalidOperationException(
            $"Cannot create a union '{Name}' directly. Use a variant constructor like {Name}.{Variants[0].Name}(...).");
    }

    public InvocationResult InvokeStaticMethod(string methodName, IReadOnlyList<object?> arguments)
    {
        if (!_variantsByName.TryGetValue(methodName, out var variant))
        {
            throw new InvalidOperationException($"Union '{Name}' has no variant named '{methodName}'.");
        }

        if (arguments.Count != variant.FieldNames.Count)
        {
            throw new InvalidOperationException(
                $"Variant '{Name}.{variant.Name}' expects {variant.FieldNames.Count} argument(s), but got {arguments.Count}.");
        }

        var fields = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < variant.FieldNames.Count; i++)
        {
            fields[variant.FieldNames[i]] = arguments[i];
        }

        var instance = new ToshUnionVariantInstance(this, variant.Name, fields);
        return new InvocationResult(instance, ReturnedVoid: false);
    }

    public bool TryGetStaticMember(string memberName, out object? value)
    {
        if (_variantsByName.TryGetValue(memberName, out var variant))
        {
            if (variant.FieldNames.Count == 0)
            {
                // Unit variant — return the instance directly
                value = new ToshUnionVariantInstance(this, variant.Name, new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase));
                return true;
            }

            // Has fields — hint that it needs to be called
            throw new InvalidOperationException(
                $"'{memberName}' is a variant of union '{Name}'. Call it with arguments: {Name}.{memberName}(...)");
        }

        value = null;
        return false;
    }

    public bool TryGetMember(string name, out object? value, bool includeHidden = false)
    {
        foreach (var field in GetMembers(includeHidden))
        {
            if (string.Equals(field.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                value = field.Value;
                return true;
            }
        }

        value = null;
        return false;
    }

    public bool TrySetMember(string name, object? value) => false;

    public IReadOnlyList<KeyValuePair<string, object?>> GetMembers(bool includeHidden = false)
    {
        return
        [
            new KeyValuePair<string, object?>("Name", ShellTypeName),
            new KeyValuePair<string, object?>("FullName", ShellFullName),
            new KeyValuePair<string, object?>("IsUnion", true),
            new KeyValuePair<string, object?>("VariantCount", Variants.Count),
        ];
    }

    public IReadOnlyList<ShellMemberDescriptor> GetShellMembers(bool includeHidden = false) =>
        Array.Empty<ShellMemberDescriptor>();

    public IReadOnlyList<ShellMethodDescriptor> GetShellMethods(bool includeHidden = false)
    {
        return Variants
            .Where(v => v.FieldNames.Count > 0)
            .Select(v => new ShellMethodDescriptor(
                v.Name,
                ReturnTypeName: Name,
                IsStatic: true,
                ParameterCount: v.FieldNames.Count,
                Signature: $"{v.Name}({string.Join(", ", v.FieldNames)})",
                IsHidden: false))
            .ToArray();
    }

    public IReadOnlyList<ShellConstructorDescriptor> GetShellConstructors() =>
        Array.Empty<ShellConstructorDescriptor>();
}

public sealed record UnionVariantDefinition(
    string Name,
    IReadOnlyList<string> FieldNames);

public sealed class ToshUnionVariantInstance : IShellRecordObject, IShellTypedObject
{
    private readonly Dictionary<string, object?> _fields;

    public ToshUnionVariantInstance(
        ToshUnionDefinition unionDefinition,
        string variantName,
        Dictionary<string, object?> fields)
    {
        UnionDefinition = unionDefinition;
        VariantName = variantName;
        _fields = fields;
    }

    public ToshUnionDefinition UnionDefinition { get; }

    public string VariantName { get; }

    public string ShellTypeName => $"{UnionDefinition.Name}.{VariantName}";

    public IShellTypeDescriptor ShellTypeDescriptor => UnionDefinition;

    public bool TryGetMember(string name, out object? value, bool includeHidden = false)
    {
        if (string.Equals(name, "Variant", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "Tag", StringComparison.OrdinalIgnoreCase))
        {
            value = VariantName;
            return true;
        }

        return _fields.TryGetValue(name, out value);
    }

    public bool TrySetMember(string name, object? value) => false;

    public IReadOnlyList<KeyValuePair<string, object?>> GetMembers(bool includeHidden = false)
    {
        var members = new List<KeyValuePair<string, object?>>
        {
            new("Variant", VariantName)
        };

        foreach (var (key, val) in _fields)
        {
            members.Add(new KeyValuePair<string, object?>(key, val));
        }

        return members;
    }

    public override string ToString()
    {
        if (_fields.Count == 0) return $"{UnionDefinition.Name}.{VariantName}";
        var fieldValues = string.Join(", ", _fields.Values.Select(v => v?.ToString() ?? "null"));
        return $"{UnionDefinition.Name}.{VariantName}({fieldValues})";
    }

    public override bool Equals(object? obj)
    {
        if (obj is not ToshUnionVariantInstance other) return false;
        if (!string.Equals(UnionDefinition.Name, other.UnionDefinition.Name, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.Equals(VariantName, other.VariantName, StringComparison.OrdinalIgnoreCase)) return false;
        if (_fields.Count != other._fields.Count) return false;

        foreach (var (key, val) in _fields)
        {
            if (!other._fields.TryGetValue(key, out var otherVal)) return false;
            if (!Equals(val, otherVal)) return false;
        }

        return true;
    }

    public override int GetHashCode()
    {
        var hash = HashCode.Combine(UnionDefinition.Name, VariantName);
        foreach (var (key, val) in _fields)
        {
            hash = HashCode.Combine(hash, key, val);
        }
        return hash;
    }
}
