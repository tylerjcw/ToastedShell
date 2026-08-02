using Tosh.Runtime;

namespace Tosh.Language;

internal sealed class ToshClassSuperReference : IShellRecordObject, IShellInvocableObject, IShellCallable
{
    private readonly ToshClassInstance _instance;
    private readonly ToshClassDefinition _baseClass;

    /// <summary>
    /// The class that reached for <c>$super</c>. A subclass may see the base's `guarded` members
    /// through it, which is what protected access is for, but not the base's `shy` ones.
    /// </summary>
    private readonly ToshClassDefinition? _accessor;

    public ToshClassSuperReference(
        ToshClassInstance instance,
        ToshClassDefinition baseClass,
        ToshClassDefinition? accessor = null)
    {
        _instance = instance;
        _baseClass = baseClass;
        _accessor = accessor;
    }

    public string ShellTypeName => _baseClass.Name;

    public string CallableName => $"super({_baseClass.Name})";

    public int RequiredParameterCount => 0;

    public int? MaximumParameterCount => null;

    public async IAsyncEnumerable<object?> InvokeAsync(CommandContext context)
    {
        await _baseClass.InvokeConstructorOnInstanceAsync(
            _instance,
            context.Arguments,
            context.CancellationToken);
        yield break;
    }

    public bool TryGetMember(string name, out object? value, bool includeHidden = false)
    {
        return _baseClass.TryGetInstanceMember(_instance, name, includeHidden: true, _accessor, out value);
    }

    public bool TrySetMember(string name, object? value)
    {
        return _baseClass.TrySetInstanceMember(_instance, name, value, includeHidden: true, _accessor);
    }

    public IReadOnlyList<KeyValuePair<string, object?>> GetMembers(bool includeHidden = false)
    {
        return _baseClass.GetInstanceMembers(_instance, includeHidden: true, _accessor);
    }

    public ValueTask<IReadOnlyList<KeyValuePair<string, object?>>> GetMembersAsync(
        bool includeHidden,
        CancellationToken cancellationToken) =>
        _baseClass.GetInstanceMembersAsync(
            _instance,
            _accessor,
            includeHidden: true,
            cancellationToken);

    public InvocationResult InvokeInstanceMethod(string methodName, IReadOnlyList<object?> arguments)
    {
        return _baseClass.InvokeInstanceMethod(_instance, methodName, arguments, includeHidden: true, _accessor);
    }

    public ValueTask<InvocationResult> InvokeInstanceMethodAsync(
        string methodName,
        IReadOnlyList<object?> arguments,
        CancellationToken cancellationToken)
    {
        return _baseClass.InvokeInstanceMethodAsync(
            _instance,
            methodName,
            arguments,
            includeHidden: true,
            _accessor,
            cancellationToken);
    }

    public ValueTask<(bool Found, object? Value)> TryGetMemberAsync(
        string name,
        bool includeHidden,
        CancellationToken cancellationToken) =>
        _baseClass.TryGetInstanceMemberAsync(
            _instance,
            name,
            includeHidden: true,
            _accessor,
            cancellationToken);

    public ValueTask<bool> TrySetMemberAsync(
        string name,
        object? value,
        CancellationToken cancellationToken) =>
        _baseClass.TrySetInstanceMemberAsync(
            _instance,
            name,
            value,
            includeHidden: true,
            _accessor,
            cancellationToken);
}
