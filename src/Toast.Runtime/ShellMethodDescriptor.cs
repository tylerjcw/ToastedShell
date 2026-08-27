namespace Tosh.Runtime;

public sealed record ShellMethodDescriptor(
    string Name,
    string ReturnTypeName,
    bool IsStatic,
    int ParameterCount,
    string Signature,
    bool IsHidden = false,
    /// <summary>
    /// The member's own `##` summary, when it has one — `TS-P2-101`. Defaulted to null so a
    /// CLR-backed descriptor is not made to declare it has no ToastScript comment.
    /// </summary>
    string? Documentation = null);
