using Tosh.Runtime;

namespace Tosh.Language;

internal sealed class ToshClassClrSuperReference : IShellRecordObject, IShellInvocableObject, IShellCallable
{
    private readonly ToshClassInstance _instance;
    private readonly Type _clrBaseType;
    private readonly ToshEngine _engine;

    public ToshClassClrSuperReference(ToshClassInstance instance, Type clrBaseType, ToshEngine engine)
    {
        _instance = instance;
        _clrBaseType = clrBaseType;
        _engine = engine;
    }

    public string ShellTypeName => _clrBaseType.Name;

    public string CallableName => $"super({_clrBaseType.Name})";

    public int RequiredParameterCount => 0;

    public int? MaximumParameterCount => null;

    public async IAsyncEnumerable<object?> InvokeAsync(CommandContext context)
    {
        var clrObject = await ToshClassDefinition.CreateClrBaseObjectAsync(
            _engine,
            _instance,
            _clrBaseType,
            context.Arguments,
            context.CancellationToken);
        if (!_instance.TryInitializeClrBase(clrObject))
        {
            throw new InvalidOperationException(
                $"CLR base class '{_clrBaseType.FullName}' has already been initialized for this instance.");
        }
        yield break;
    }

    public bool TryGetMember(string name, out object? value, bool includeHidden = false)
    {
        if (_instance.ClrBaseObject is not null)
        {
            try
            {
                value = _engine.LanguageRuntime.ObjectAccessor.GetValue(_instance.ClrBaseObject, name);
                return true;
            }
            catch { /* member not found */ }
        }

        value = null;
        return false;
    }

    public bool TrySetMember(string name, object? value)
    {
        if (_instance.ClrBaseObject is not null)
        {
            try
            {
                _engine.LanguageRuntime.ObjectAccessor.SetValue(_instance.ClrBaseObject, name, value);
                return true;
            }
            catch { /* member not found or read-only */ }
        }

        return false;
    }

    public IReadOnlyList<KeyValuePair<string, object?>> GetMembers(bool includeHidden = false)
    {
        if (_instance.ClrBaseObject is not null)
        {
            var result = new List<KeyValuePair<string, object?>>();
            foreach (var prop in _clrBaseType.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                try { result.Add(new KeyValuePair<string, object?>(prop.Name, prop.GetValue(_instance.ClrBaseObject))); }
                catch { result.Add(new KeyValuePair<string, object?>(prop.Name, null)); }
            }
            return result;
        }

        return [];
    }

    /// <summary>
    /// The name to invoke on the base object so that <c>$super.M()</c> reaches the base's
    /// implementation of <c>M</c>.
    /// </summary>
    /// <remarks>
    /// Where the base object is an emitted subclass, <c>M</c> is the override and calling it
    /// would dispatch straight back into the tōsh method that asked — <c>$super.ToString()</c>
    /// inside <c>func ToString()</c> recursing until the depth guard stopped it. The factory
    /// pairs every override with a non-virtual thunk that reaches past it, and this is what
    /// asks for it. A base with no such thunk is not an emitted subclass, and the name as
    /// written is already the base's own.
    /// </remarks>
    private string BaseName(string methodName)
    {
        if (_instance.ClrBaseObject is not Bridge.IToshClrBackedObject)
        {
            return methodName;
        }

        var thunk = methodName + Bridge.ToshClrSubclassFactory.BaseThunkSuffix;

        return _instance.ClrBaseObject.GetType().GetMethod(thunk) is not null ? thunk : methodName;
    }

    public InvocationResult InvokeInstanceMethod(string methodName, IReadOnlyList<object?> arguments)
    {
        if (_instance.ClrBaseObject is not null)
        {
            var result = _engine.LanguageRuntime.Invoker.InvokeInstance(
                _instance.ClrBaseObject,
                BaseName(methodName),
                arguments);
            return new InvocationResult(result, ReturnedVoid: false);
        }

        throw new InvalidOperationException($"CLR base object has not been initialized. Call $super(...) first.");
    }

    public async ValueTask<InvocationResult> InvokeInstanceMethodAsync(
        string methodName,
        IReadOnlyList<object?> arguments,
        CancellationToken cancellationToken)
    {
        if (_instance.ClrBaseObject is not null)
        {
            return await _engine.LanguageRuntime.Invoker.InvokeInstanceMethodAsync(
                _instance.ClrBaseObject,
                BaseName(methodName),
                arguments,
                cancellationToken);
        }

        throw new InvalidOperationException($"CLR base object has not been initialized. Call $super(...) first.");
    }
}
