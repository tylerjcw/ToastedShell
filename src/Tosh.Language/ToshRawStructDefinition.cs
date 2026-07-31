using System.Runtime.InteropServices;

using Tosh.Runtime;

namespace Tosh.Language;

/// <summary>
/// A thin <see cref="IShellNamedType"/> façade over the CLR type emitted for a
/// <c>raw struct</c>, so `new SysInfo()`, `describe-type SysInfo`, and
/// `members SysInfo` behave like every other named type.
///
/// This is <em>not</em> a second representation of the layout — the emitted
/// <see cref="ClrType"/> is the single source of truth, and every member here
/// reads from it or from the <see cref="RawStructLayoutPlan"/> that produced it.
/// Instances are plain boxed CLR structs; <c>ReflectionObjectAccessor</c>
/// already resolves their fields case-insensitively, so `$info.Uptime` finds
/// `uptime` with no wrapper in the way.
/// </summary>
internal sealed class ToshRawStructDefinition : IShellNamedType, INativeLayoutType
{
    private readonly IReadOnlyDictionary<string, object?> _defaults;

    public ToshRawStructDefinition(
        RawStructLayoutPlan plan,
        Type clrType,
        IReadOnlyDictionary<string, object?> defaults)
    {
        Plan = plan;
        ClrType = clrType;
        _defaults = defaults;
    }

    public RawStructLayoutPlan Plan { get; }

    public Type ClrType { get; }

    public string Name => Plan.Name;

    public int Size => Marshal.SizeOf(ClrType);

    public string ShellTypeName => Plan.Name;

    public string ShellFullName => Plan.Name;

    public string? ShellNamespace => null;

    public string? ShellAssemblyName => "ToSh";

    public string? ShellBaseTypeName => typeof(ValueType).FullName;

    public bool ShellIsClass => false;

    public bool ShellIsInterface => false;

    public bool ShellIsEnum => false;

    public bool ShellIsValueType => true;

    public bool ShellIsAbstract => false;

    public bool ShellIsGenericType => false;

    public bool ShellIsArray => false;

    public bool ShellIsPublic => true;

    /// <summary>
    /// Constructs a zeroed value and applies field defaults.
    ///
    /// Defaults live here rather than on the emitted type because a CLR struct
    /// cannot carry an initializer the marshaller would honour —
    /// <c>Marshal.PtrToStructure</c> and <c>default(T)</c> both bypass
    /// constructors. So a struct arriving from an <c>out</c> parameter is
    /// zero-filled and never sees these, which is correct: <c>out</c> means the
    /// callee writes everything, and seeding defaults there would mask a callee
    /// that failed to write.
    /// </summary>
    public object CreateInstance(IReadOnlyList<object?> arguments)
    {
        var instance = Activator.CreateInstance(ClrType)
                       ?? throw new InvalidOperationException($"Unable to construct raw struct '{Name}'.");

        foreach (var (fieldName, defaultValue) in _defaults)
        {
            AssignField(instance, fieldName, defaultValue);
        }

        // Positional arguments override defaults, in declaration order.
        for (var index = 0; index < arguments.Count && index < Plan.Fields.Count; index++)
        {
            AssignField(instance, Plan.Fields[index].Name, arguments[index]);
        }

        return instance;
    }

    private void AssignField(object instance, string fieldName, object? value)
    {
        var field = ClrType.GetField(fieldName);
        if (field is null) return;

        if (value is null)
        {
            return;
        }

        if (TypeConversion.TryConvert(value, field.FieldType, out var converted))
        {
            field.SetValue(instance, converted);
        }
    }

    public InvocationResult InvokeStaticMethod(string methodName, IReadOnlyList<object?> arguments) =>
        throw new InvalidOperationException($"Raw struct '{Name}' has no static methods — it is a memory layout.");

    public bool TryGetStaticMember(string memberName, out object? value)
    {
        value = null;
        return false;
    }

    public bool TryGetMember(string name, out object? value, bool includeHidden = false)
    {
        value = name switch
        {
            "Name" => ShellTypeName,
            "FullName" => ShellFullName,
            "Namespace" => ShellNamespace,
            "Assembly" => ShellAssemblyName,
            "BaseType" => ShellBaseTypeName,
            "IsValueType" => true,
            "FieldCount" => Plan.Fields.Count,
            "Size" => Size,
            "Layout" => Plan.Kind.ToString(),
            _ => null,
        };

        return value is not null;
    }

    public bool TrySetMember(string name, object? value) => false;

    public IReadOnlyList<KeyValuePair<string, object?>> GetMembers(bool includeHidden = false) =>
    [
        new("Name", ShellTypeName),
        new("FullName", ShellFullName),
        new("Namespace", ShellNamespace),
        new("Assembly", ShellAssemblyName),
        new("BaseType", ShellBaseTypeName),
        new("IsValueType", true),
        new("FieldCount", Plan.Fields.Count),
        new("Size", Size),
        new("Layout", Plan.Kind.ToString()),
    ];

    public IReadOnlyList<ShellMemberDescriptor> GetShellMembers(bool includeHidden = false) =>
        Plan.Fields
            .Select(field => new ShellMemberDescriptor(
                field.Name,
                Kind: "Field",
                TypeName: DescribeFieldType(field),
                IsStatic: false,
                IsWritable: true,
                IsHidden: false))
            .ToArray();

    public IReadOnlyList<ShellMethodDescriptor> GetShellMethods(bool includeHidden = false) =>
        Array.Empty<ShellMethodDescriptor>();

    /// <summary>
    /// One positional constructor over the declared fields, matching how
    /// <see cref="CreateInstance"/> assigns them. `new SysInfo()` with no
    /// arguments is the common case — a raw struct is usually filled by the
    /// callee, not built by hand.
    /// </summary>
    public IReadOnlyList<ShellConstructorDescriptor> GetShellConstructors() =>
    [
        new(Plan.Fields.Count,
            $"{Name}({string.Join(", ", Plan.Fields.Select(DescribeFieldType))})"),
    ];

    // Without this, a qualified reference renders (and stringifies) as
    // "Tosh.Language.ToshRawStructDefinition".
    public override string ToString() => Name;

    private static string DescribeFieldType(RawStructFieldPlan field) =>
        field.SizeConst is { } count
            ? $"{field.ClrType.GetElementType()?.Name ?? field.ClrType.Name}[{count}]"
            : field.ClrType.Name;
}
