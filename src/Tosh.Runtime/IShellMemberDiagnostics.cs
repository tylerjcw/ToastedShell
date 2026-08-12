namespace Tosh.Runtime;

/// <summary>
/// A value that can explain why one of its members could not be reached — <c>TS-P2-18</c>.
/// </summary>
/// <remarks>
/// Member access fell through to reflection when a shell object declined a name, and reflection
/// can only report what it sees: "was not found". A <c>shy</c> member that exists and was refused
/// is therefore indistinguishable from one that was never declared, which sends the reader
/// looking for a typo instead of at the modifier. Only the value knows the difference, so it is
/// asked before the generic message is composed.
/// </remarks>
public interface IShellMemberDiagnostics
{
    /// <summary>
    /// The message for <paramref name="name"/>, or <see langword="null"/> to accept the caller's
    /// generic "was not found".
    /// </summary>
    string? ExplainMissingMember(string name);
}
