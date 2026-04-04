namespace Tosh.Core;

public sealed record ShellMemberDescriptor(
    string Name,
    string Kind,
    string TypeName,
    bool IsStatic,
    bool IsWritable,
    bool IsHidden = false);
