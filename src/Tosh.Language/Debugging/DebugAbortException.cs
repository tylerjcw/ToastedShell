using Tosh.Runtime;
using Tosh.Language.Parsing;

namespace Tosh.Language.Debugging;

/// <summary>
/// Thrown when a debug hook requests aborting the current execution.
/// </summary>
public sealed class DebugAbortException : Exception
{
    public DebugAbortException() : base("Execution aborted by debugger.") { }

    public TextSpan Span { get; init; }
}
