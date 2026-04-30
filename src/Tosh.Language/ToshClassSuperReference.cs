using Tosh.Runtime;

namespace Tosh.Language;

internal sealed class ToshClassSuperReference : IShellRecordObject, IShellInvocableObject, IShellCallable
{
    private readonly ToshClassInstance _instance;
    private readonly ToshClassDefinition _baseClass;

    public ToshClassSuperReference(ToshClassInstance instance, ToshClassDefinition baseClass)
    {
        _instance = instance;
        _baseClass = baseClass;
    }

    public string ShellTypeName => _baseClass.Name;

    public string CallableName => $"super({_baseClass.Name})";

    public int RequiredParameterCount => 0;

    public int? MaximumParameterCount => null;

    public async IAsyncEnumerable<object?> InvokeAsync(CommandContext context)
    {
        _baseClass.InvokeConstructorOnInstance(_instance, context.Arguments);
        _instance.MarkSuperCalled();
        yield break;
    }

    public bool TryGetMember(string name, out object? value, bool includeHidden = false)
    {
        return _baseClass.TryGetInstanceMember(_instance, name, includeHidden: true, out value);
    }

    public bool TrySetMember(string name, object? value)
    {
        return _baseClass.TrySetInstanceMember(_instance, name, value, includeHidden: true);
    }

    public IReadOnlyList<KeyValuePair<string, object?>> GetMembers(bool includeHidden = false)
    {
        return _baseClass.GetInstanceMembers(_instance, includeHidden: true);
    }

    public InvocationResult InvokeInstanceMethod(string methodName, IReadOnlyList<object?> arguments)
    {
        return _baseClass.InvokeInstanceMethod(_instance, methodName, arguments, includeHidden: true);
    }
}
