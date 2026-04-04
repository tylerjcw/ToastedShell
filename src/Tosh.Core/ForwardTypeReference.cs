namespace Tosh.Core;

/// <summary>
/// Lightweight placeholder registered during the first pass of type registration.
/// Replaced by the real definition when the type statement is fully evaluated.
/// </summary>
public sealed class ForwardTypeReference : IShellNamedType
{
    public ForwardTypeReference(string name)
    {
        ShellTypeName = name;
    }

    public string ShellTypeName { get; }

    public string ShellFullName => ShellTypeName;

    public string? ShellNamespace => null;

    public string? ShellAssemblyName => null;

    public string? ShellBaseTypeName => null;

    public bool ShellIsClass => true;

    public bool ShellIsInterface => false;

    public bool ShellIsEnum => false;

    public bool ShellIsValueType => false;

    public bool ShellIsAbstract => false;

    public bool ShellIsGenericType => false;

    public bool ShellIsArray => false;

    public bool ShellIsPublic => true;

    public object CreateInstance(IReadOnlyList<object?> arguments) =>
        throw new InvalidOperationException($"Type '{ShellTypeName}' has not been fully defined yet.");

    public InvocationResult InvokeStaticMethod(string methodName, IReadOnlyList<object?> arguments) =>
        throw new InvalidOperationException($"Type '{ShellTypeName}' has not been fully defined yet.");

    public bool TryGetStaticMember(string memberName, out object? value)
    {
        value = null;
        return false;
    }

    public bool TryGetMember(string name, out object? value, bool includeHidden = false)
    {
        value = null;
        return false;
    }

    public bool TrySetMember(string name, object? value) => false;

    public IReadOnlyList<KeyValuePair<string, object?>> GetMembers(bool includeHidden = false) => [];

    public IReadOnlyList<ShellMemberDescriptor> GetShellMembers(bool includeHidden = false) => [];

    public IReadOnlyList<ShellMethodDescriptor> GetShellMethods(bool includeHidden = false) => [];

    public IReadOnlyList<ShellConstructorDescriptor> GetShellConstructors() => [];
}
