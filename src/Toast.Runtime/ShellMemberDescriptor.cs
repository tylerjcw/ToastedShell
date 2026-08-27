namespace Tosh.Runtime;

public sealed record ShellMemberDescriptor(
    string Name,
    string Kind,
    string TypeName,
    bool IsStatic,
    bool IsWritable,
    bool IsHidden = false,
    /// <inheritdoc cref="ShellMethodDescriptor.Documentation" />
    string? Documentation = null);
