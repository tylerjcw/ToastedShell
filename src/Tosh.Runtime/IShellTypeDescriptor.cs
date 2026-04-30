namespace Tosh.Runtime;

public interface IShellTypeDescriptor : IShellRecordObject
{
    string ShellFullName { get; }

    string? ShellNamespace { get; }

    string? ShellAssemblyName { get; }

    string? ShellBaseTypeName { get; }

    bool ShellIsClass { get; }

    bool ShellIsInterface { get; }

    bool ShellIsEnum { get; }

    bool ShellIsValueType { get; }

    bool ShellIsAbstract { get; }

    bool ShellIsGenericType { get; }

    bool ShellIsArray { get; }

    bool ShellIsPublic { get; }

    IReadOnlyList<ShellMemberDescriptor> GetShellMembers(bool includeHidden = false);

    IReadOnlyList<ShellMethodDescriptor> GetShellMethods(bool includeHidden = false);

    IReadOnlyList<ShellConstructorDescriptor> GetShellConstructors();
}
