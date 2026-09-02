using Tosh.Language.Parsing;
using Tosh.Runtime;

namespace Tosh.Language;

public sealed class ToshEnumDefinition : IShellNamedType, IShellFlagsEnum
{
    /// <summary>The declaration's own `##` documentation (`TS-P2-101`).</summary>
    public DocComment? Documentation { get; internal set; }

    /// <inheritdoc />
    public string? ShellDocumentation => Documentation?.Description is { Length: > 0 } summary
        ? summary
        : null;

    private readonly Dictionary<string, ToshEnumValue> _membersByName;

    public ToshEnumDefinition(
        string name,
        string? underlyingTypeName,
        Type underlyingType,
        IReadOnlyList<ToshEnumValue> members,
        string sourceName,
        string sourceText,
        TextSpan span,
        DocComment? documentation = null)
    {
        Documentation = documentation;
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

    /// <summary>
    /// Whether the declaration said <c>flags</c> (`TS-P3-14`).
    /// </summary>
    public bool IsFlags { get; internal set; }

    /// <summary>
    /// The member standing for a combined value.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The name is composed from the bits rather than concatenated when the
    /// combination is made, so `band` and `bxor` name their results correctly too,
    /// and the order is always declaration order rather than the order the caller
    /// happened to write.
    /// </para>
    /// <para>
    /// Zero-valued members are skipped when other bits are present — a `None = 0`
    /// is covered by every value, and listing it beside real flags would be noise —
    /// but answer on their own when the value is zero. Bits belonging to no member
    /// leave the value unnamed and it renders as the number, which is the truthful
    /// answer for a flag the declaration does not know about.
    /// </para>
    /// </remarks>
    public object FromFlags(long value)
    {
        var names = new List<string>();
        var covered = 0L;

        foreach (var member in Members)
        {
            var bits = ToBits(member.UnderlyingValue);

            if (bits == 0)
            {
                continue;
            }

            if ((value & bits) == bits)
            {
                names.Add(member.Name);
                covered |= bits;
            }
        }

        if (value == 0)
        {
            var zero = Members.FirstOrDefault(
                member => ToBits(member.UnderlyingValue) == 0);

            return zero.Name is null
                ? ConvertToUnderlying(value)
                : new ToshEnumValue(this, zero.Name, zero.UnderlyingValue);
        }

        return covered == value && names.Count > 0
            ? new ToshEnumValue(this, string.Join(", ", names), ConvertToUnderlying(value))
            : ConvertToUnderlying(value);
    }

    /// <summary>The member's backing value as a whole number, or zero if it has none.</summary>
    private static long ToBits(object? underlying)
    {
        try
        {
            return underlying is null
                ? 0
                : Convert.ToInt64(underlying, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException)
        {
            return 0;
        }
    }

    /// <summary>Narrows a combined value back to the enum's declared backing type.</summary>
    private object ConvertToUnderlying(long value)
    {
        try
        {
            return Convert.ChangeType(value, UnderlyingType, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception exception) when (exception is OverflowException or InvalidCastException)
        {
            return value;
        }
    }

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

        if (string.Equals(methodName, "values", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(methodName, "names", StringComparison.OrdinalIgnoreCase))
        {
            if (arguments.Count != 0)
            {
                throw new InvalidOperationException(
                    $"Enum method '{Name}.{methodName}' expects no arguments.");
            }

            var value = string.Equals(methodName, "values", StringComparison.OrdinalIgnoreCase)
                ? GetUnderlyingValues()
                : Members.Select(member => member.Name).ToArray();

            return new InvocationResult(value, ReturnedVoid: false);
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
                "names",
                ReturnTypeName: "string[]",
                IsStatic: true,
                ParameterCount: 0,
                Signature: "string[] names()",
                IsHidden: false),
            new ShellMethodDescriptor(
                "parse",
                ReturnTypeName: Name,
                IsStatic: true,
                ParameterCount: 1,
                Signature: $"{Name} parse(object value)",
                IsHidden: false),
            new ShellMethodDescriptor(
                "values",
                ReturnTypeName: $"{UnderlyingTypeName}[]",
                IsStatic: true,
                ParameterCount: 0,
                Signature: $"{UnderlyingTypeName}[] values()",
                IsHidden: false),
        ];
    }

    private Array GetUnderlyingValues()
    {
        var values = Array.CreateInstance(UnderlyingType, Members.Count);

        for (var index = 0; index < Members.Count; index++)
        {
            values.SetValue(Members[index].UnderlyingValue, index);
        }

        return values;
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
