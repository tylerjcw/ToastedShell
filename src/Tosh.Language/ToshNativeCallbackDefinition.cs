using System.Runtime.InteropServices;

using Tosh.Language.Bridge;
using Tosh.Runtime;

namespace Tosh.Language;

/// <summary>
/// A <see cref="IShellNamedType"/> façade over the delegate type emitted for a
/// <c>raw callback</c>, so the name resolves wherever a native interop type is
/// expected — exactly as <see cref="ToshRawStructDefinition"/> does for a
/// <c>raw struct</c>.
///
/// The emitted <see cref="ClrType"/> is the single source of truth for the ABI;
/// <see cref="Parameters"/> is kept only so the thunk can convert incoming
/// native arguments back into ToSh values, which needs the declared type names
/// rather than just the CLR types.
///
/// It is deliberately not constructible from script — <c>new Comparator()</c>
/// is meaningless. A callback value is produced by passing a ToSh function
/// where the callback type is expected.
/// </summary>
internal sealed class ToshNativeCallbackDefinition : IShellNamedType
{
    public ToshNativeCallbackDefinition(
        string name,
        Type clrType,
        IReadOnlyList<NativeFunctionParameterDefinition> parameters,
        NativeFunctionReturnDefinition @return,
        CallingConvention callingConvention)
    {
        Name = name;
        ClrType = clrType;
        Parameters = parameters;
        Return = @return;
        CallingConvention = callingConvention;
    }

    public string Name { get; }

    public Type ClrType { get; }

    public IReadOnlyList<NativeFunctionParameterDefinition> Parameters { get; }

    public NativeFunctionReturnDefinition Return { get; }

    public CallingConvention CallingConvention { get; }

    public string Signature
    {
        get
        {
            var parameters = Parameters.Select(static parameter => parameter.PassingMode switch
            {
                NativeParameterPassingMode.Out => $"out {parameter.Name}: {parameter.TypeName}",
                NativeParameterPassingMode.Ref => $"ref {parameter.Name}: {parameter.TypeName}",
                _ => $"{parameter.Name}: {parameter.TypeName}",
            });

            return $"{Name}({string.Join(", ", parameters)}) -> {Return.TypeName}";
        }
    }

    public string ShellTypeName => Name;

    public string ShellFullName => Name;

    public string? ShellNamespace => null;

    public string? ShellAssemblyName => "ToSh";

    public string? ShellBaseTypeName => typeof(MulticastDelegate).FullName;

    public bool ShellIsClass => true;

    public bool ShellIsInterface => false;

    public bool ShellIsEnum => false;

    public bool ShellIsValueType => false;

    public bool ShellIsAbstract => false;

    public bool ShellIsGenericType => false;

    public bool ShellIsArray => false;

    public bool ShellIsPublic => true;

    public object CreateInstance(IReadOnlyList<object?> arguments) =>
        throw new InvalidOperationException(
            $"'{Name}' is a callback type, not a constructible value — pass a function where it is expected.");

    public InvocationResult InvokeStaticMethod(string methodName, IReadOnlyList<object?> arguments) =>
        throw new InvalidOperationException($"Callback type '{Name}' has no static methods.");

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
            "ParameterCount" => Parameters.Count,
            "ReturnType" => Return.TypeName,
            "CallingConvention" => CallingConvention.ToString(),
            "Signature" => Signature,
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
        new("ParameterCount", Parameters.Count),
        new("ReturnType", Return.TypeName),
        new("CallingConvention", CallingConvention.ToString()),
        new("Signature", Signature),
    ];

    public IReadOnlyList<ShellMemberDescriptor> GetShellMembers(bool includeHidden = false) =>
        Parameters
            .Select(parameter => new ShellMemberDescriptor(
                parameter.Name,
                Kind: "Parameter",
                TypeName: parameter.TypeName,
                IsStatic: false,
                IsWritable: false,
                IsHidden: false))
            .ToArray();

    public IReadOnlyList<ShellMethodDescriptor> GetShellMethods(bool includeHidden = false) =>
        Array.Empty<ShellMethodDescriptor>();

    // A callback type is never constructed — see CreateInstance.
    public IReadOnlyList<ShellConstructorDescriptor> GetShellConstructors() =>
        Array.Empty<ShellConstructorDescriptor>();

    public override string ToString() => Signature;
}
