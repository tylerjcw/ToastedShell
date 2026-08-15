namespace Tosh.Runtime;

public interface IShellTypeDescriptor : IShellRecordObject
{
    string ShellFullName { get; }

    string? ShellNamespace { get; }

    string? ShellAssemblyName { get; }

    string? ShellBaseTypeName { get; }

    /// <summary>
    /// The declaration's own documentation, when it has any.
    /// </summary>
    /// <remarks>
    /// `TS-P2-101`. A `##` block on a class parsed and reached the LSP index but
    /// stopped at the runtime definition, so a library documented to the house
    /// standard was discoverable in the editor and invisible in the shell.
    ///
    /// Defaulted to null rather than added to every implementor: a CLR-backed
    /// descriptor has no ToastScript doc comment to offer, and making them all
    /// say so would be ceremony.
    /// </remarks>
    string? ShellDocumentation => null;

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
