using Tosh.Runtime;

namespace Tosh.Language;

internal sealed class ToshClassSelfReference : IShellRecordObject, IShellInvocableObject, IShellTypedObject
{
    private readonly ToshClassInstance _instance;

    public ToshClassSelfReference(ToshClassInstance instance)
    {
        _instance = instance;
    }

    public string ShellTypeName => _instance.ShellTypeName;

    public IShellTypeDescriptor ShellTypeDescriptor => _instance.Definition;

    public bool TryGetMember(string name, out object? value, bool includeHidden = false)
    {
        return _instance.Definition.TryGetInstanceMember(_instance, name, includeHidden: true, out value);
    }

    public bool TrySetMember(string name, object? value)
    {
        return _instance.Definition.TrySetInstanceMember(_instance, name, value, includeHidden: true);
    }

    public IReadOnlyList<KeyValuePair<string, object?>> GetMembers(bool includeHidden = false)
    {
        return _instance.Definition.GetInstanceMembers(_instance, includeHidden: true);
    }

    public ValueTask<IReadOnlyList<KeyValuePair<string, object?>>> GetMembersAsync(
        bool includeHidden,
        CancellationToken cancellationToken) =>
        _instance.Definition.GetInstanceMembersAsync(
            _instance,
            includeHidden: true,
            cancellationToken);

    public InvocationResult InvokeInstanceMethod(string methodName, IReadOnlyList<object?> arguments)
    {
        return _instance.Definition.InvokeInstanceMethod(_instance, methodName, arguments, includeHidden: true);
    }

    public ValueTask<InvocationResult> InvokeInstanceMethodAsync(
        string methodName,
        IReadOnlyList<object?> arguments,
        CancellationToken cancellationToken)
    {
        return _instance.Definition.InvokeInstanceMethodAsync(
            _instance,
            methodName,
            arguments,
            includeHidden: true,
            cancellationToken);
    }

    public ValueTask<(bool Found, object? Value)> TryGetMemberAsync(
        string name,
        bool includeHidden,
        CancellationToken cancellationToken) =>
        _instance.Definition.TryGetInstanceMemberAsync(
            _instance,
            name,
            includeHidden: true,
            cancellationToken);

    public ValueTask<bool> TrySetMemberAsync(
        string name,
        object? value,
        CancellationToken cancellationToken) =>
        _instance.Definition.TrySetInstanceMemberAsync(
            _instance,
            name,
            value,
            includeHidden: true,
            cancellationToken);

    public ToshClassInstance Unwrap() => _instance;
}
