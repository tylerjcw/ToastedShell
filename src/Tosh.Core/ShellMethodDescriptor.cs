namespace Tosh.Core;

public sealed record ShellMethodDescriptor(
    string Name,
    string ReturnTypeName,
    bool IsStatic,
    int ParameterCount,
    string Signature,
    bool IsHidden = false);
