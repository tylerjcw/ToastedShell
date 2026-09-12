using Tosh.Runtime;

namespace Tosh.Language;

internal sealed class ToshClassSelfReference : IShellRecordObject, IShellInvocableObject, IShellTypedObject
{
    private readonly ToshClassInstance _instance;

    /// <summary>
    /// The class whose code is running — not the instance's own class, which may be a subclass.
    /// Lookups still *start* at the instance's class so an override wins, but a `shy` member is
    /// visible only while this is the class that declared it. Without it, a base method reading
    /// its own private member and a subclass reading that same member were indistinguishable, and
    /// both were allowed.
    /// </summary>
    private readonly ToshClassDefinition? _accessor;

    public ToshClassSelfReference(ToshClassInstance instance, ToshClassDefinition? accessor = null)
    {
        _instance = instance;
        _accessor = accessor;
    }

    public string ShellTypeName => _instance.ShellTypeName;

    /// <remarks>
    /// The instance's own descriptor, not its open definition. Reading the definition
    /// meant a generic class could not see what it had been closed over from the
    /// inside: `type-of $v` answered `Box&lt;Int32&gt;` and carried `TypeArguments`,
    /// while `type-of $this` in the same class answered a bare `Box` and carried
    /// none — so the only way to recover `T` from within was to ask a member that
    /// happened to be typed `T`.
    /// </remarks>
    public IShellTypeDescriptor ShellTypeDescriptor => _instance.ShellTypeDescriptor;

    /// <summary>
    /// What this instance's type parameters are closed over, seen from the class whose code
    /// is running — <c>TOAST-0116</c>.
    /// </summary>
    /// <remarks>
    /// Keyed on the accessor rather than the instance's own class so that a base method
    /// writing <c>new Base&lt;T&gt;</c> resolves <c>T</c> as <em>Base</em> declared it, even
    /// when the instance is a subclass that named its parameter something else or supplied
    /// a concrete type for it.
    /// </remarks>
    internal IReadOnlyDictionary<string, Type?>? TypeArgumentBindings =>
        _instance.GetBindingsFor(_accessor ?? _instance.Definition);

    /// <summary>
    /// The same bindings by name, for a type argument that names a ToastScript class and so
    /// has no CLR type to bind (<c>TOAST-0131</c>).
    /// </summary>
    internal IReadOnlyDictionary<string, string>? NominalTypeArgumentBindings =>
        _instance.GetNominalBindingsFor(_accessor ?? _instance.Definition);

    public bool TryGetMember(string name, out object? value, bool includeHidden = false)
    {
        return _instance.Definition.TryGetInstanceMember(_instance, name, includeHidden: true, _accessor, out value);
    }

    public bool TrySetMember(string name, object? value)
    {
        return _instance.Definition.TrySetInstanceMember(_instance, name, value, includeHidden: true, _accessor);
    }

    public IReadOnlyList<KeyValuePair<string, object?>> GetMembers(bool includeHidden = false)
    {
        return _instance.Definition.GetInstanceMembers(_instance, includeHidden: true, _accessor);
    }

    public ValueTask<IReadOnlyList<KeyValuePair<string, object?>>> GetMembersAsync(
        bool includeHidden,
        CancellationToken cancellationToken) =>
        _instance.Definition.GetInstanceMembersAsync(
            _instance,
            _accessor,
            includeHidden: true,
            cancellationToken);

    public InvocationResult InvokeInstanceMethod(string methodName, IReadOnlyList<object?> arguments)
    {
        return _instance.Definition.InvokeInstanceMethod(_instance, methodName, arguments, includeHidden: true, _accessor);
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
            _accessor,
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
            _accessor,
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
            _accessor,
            cancellationToken);

    public ToshClassInstance Unwrap() => _instance;
}
