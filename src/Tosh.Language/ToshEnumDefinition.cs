using Tosh.Core;

namespace Tosh.Language;

public sealed class ToshEnumDefinition : IShellNamedType
{
    private readonly Dictionary<string, ToshEnumValue> _membersByName;

    public ToshEnumDefinition(
        string name,
        string? underlyingTypeName,
        Type underlyingType,
        IReadOnlyList<ToshEnumValue> members,
        string sourceName,
        string sourceText,
        TextSpan span)
    {
        Name = name;
        UnderlyingTypeName = underlyingTypeName ?? underlyingType.Name;
        UnderlyingType = underlyingType;
        Members = members;
        SourceName = sourceName;
        SourceText = sourceText;
        Span = span;
        _membersByName = members.ToDictionary(member => member.Name, StringComparer.OrdinalIgnoreCase);
    }

    public string Name { get; }

    public string UnderlyingTypeName { get; }

    public Type UnderlyingType { get; }

    public IReadOnlyList<ToshEnumValue> Members { get; }

    public string SourceName { get; }

    public string SourceText { get; }

    public TextSpan Span { get; }

    public string ShellTypeName => Name;

    public string ShellFullName => Name;

    public string? ShellNamespace => null;

    public string? ShellAssemblyName => "ToSh";

    public string? ShellBaseTypeName => typeof(Enum).FullName;

    public bool ShellIsClass => false;

    public bool ShellIsInterface => false;

    public bool ShellIsEnum => true;

    public bool ShellIsValueType => true;

    public bool ShellIsAbstract => false;

    public bool ShellIsGenericType => false;

    public bool ShellIsArray => false;

    public bool ShellIsPublic => true;

    public object CreateInstance(IReadOnlyList<object?> arguments)
    {
        if (arguments.Count != 1)
        {
            throw new InvalidOperationException($"Enum '{Name}' expects exactly one argument.");
        }

        if (TryConvertValue(arguments[0], out var value))
        {
            return value;
        }

        throw new InvalidOperationException($"Value '{arguments[0]}' could not be converted to enum '{Name}'.");
    }

    public InvocationResult InvokeStaticMethod(string methodName, IReadOnlyList<object?> arguments)
    {
        if (string.Equals(methodName, "parse", StringComparison.OrdinalIgnoreCase))
        {
            return new InvocationResult(CreateInstance(arguments), ReturnedVoid: false);
        }

        throw new InvalidOperationException($"Static method '{methodName}' was not found on enum '{Name}'.");
    }

    public bool TryGetStaticMember(string memberName, out object? value)
    {
        if (_membersByName.TryGetValue(memberName, out var member))
        {
            value = member;
            return true;
        }

        value = null;
        return false;
    }

    public bool TryGetMember(string name, out object? value, bool includeHidden = false)
    {
        if (TryGetStaticMember(name, out value))
        {
            return true;
        }

        value = name switch
        {
            "Name" => ShellTypeName,
            "FullName" => ShellFullName,
            "Namespace" => ShellNamespace,
            "Assembly" => ShellAssemblyName,
            "BaseType" => ShellBaseTypeName,
            "UnderlyingType" => UnderlyingTypeName,
            "MemberCount" => Members.Count,
            _ => null,
        };

        return value is not null;
    }

    public bool TrySetMember(string name, object? value) => false;

    public IReadOnlyList<KeyValuePair<string, object?>> GetMembers(bool includeHidden = false)
    {
        return
        [
            new KeyValuePair<string, object?>("Name", ShellTypeName),
            new KeyValuePair<string, object?>("FullName", ShellFullName),
            new KeyValuePair<string, object?>("Namespace", ShellNamespace),
            new KeyValuePair<string, object?>("Assembly", ShellAssemblyName),
            new KeyValuePair<string, object?>("BaseType", ShellBaseTypeName),
            new KeyValuePair<string, object?>("UnderlyingType", UnderlyingTypeName),
            new KeyValuePair<string, object?>("MemberCount", Members.Count),
        ];
    }

    public IReadOnlyList<ShellMemberDescriptor> GetShellMembers(bool includeHidden = false)
    {
        return Members
            .Select(member => new ShellMemberDescriptor(
                member.Name,
                Kind: "EnumMember",
                TypeName: UnderlyingTypeName,
                IsStatic: true,
                IsWritable: false,
                IsHidden: false))
            .ToArray();
    }

    public IReadOnlyList<ShellMethodDescriptor> GetShellMethods(bool includeHidden = false)
    {
        return
        [
            new ShellMethodDescriptor(
                "parse",
                ReturnTypeName: Name,
                IsStatic: true,
                ParameterCount: 1,
                Signature: $"{Name} parse(object value)",
                IsHidden: false),
        ];
    }

    public IReadOnlyList<ShellConstructorDescriptor> GetShellConstructors()
    {
        return
        [
            new ShellConstructorDescriptor(1, $"{Name}(object value)"),
        ];
    }

    public bool TryConvertValue(object? value, out ToshEnumValue enumValue)
    {
        if (value is ToshEnumValue other &&
            string.Equals(other.Definition.Name, Name, StringComparison.Ordinal))
        {
            enumValue = other;
            return true;
        }

        if (value is string text)
        {
            if (_membersByName.TryGetValue(text, out var directMatch))
            {
                enumValue = directMatch;
                return true;
            }
        }

        foreach (var member in Members)
        {
            if (OperatorEvaluator.AreEqual(member.UnderlyingValue, value))
            {
                enumValue = member;
                return true;
            }
        }

        enumValue = default;
        return false;
    }
}
