namespace Tosh.Runtime;

/// <summary>
/// A value was indexed, or assigned through an index, and has no way to answer —
/// <c>TOAST-0117</c>.
/// </summary>
/// <remarks>
/// Distinct from the range and conversion failures beside it because it is the one an
/// interested caller can improve on. Indexing utilities know only what the value is; the
/// engine knows that a ToastScript class could have said how it is indexed and did not, so
/// it replaces this with a message naming the member to declare. Matching on the message
/// text would have done the same job and broken the first time the wording changed.
/// </remarks>
public sealed class ShellIndexNotSupportedException : InvalidOperationException
{
    public ShellIndexNotSupportedException(string message, bool isAssignment)
        : base(message)
    {
        IsAssignment = isAssignment;
    }

    /// <summary>Whether the failure was a write rather than a read.</summary>
    public bool IsAssignment { get; }
}
