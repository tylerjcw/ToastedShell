using Tosh.Core;
using Tosh.Language.Parsing;

namespace Tosh.Language.Debugging;

/// <summary>
/// Contextual information passed to a debug hook before each statement executes.
/// </summary>
public sealed class DebugStepContext
{
    public required string SourceName { get; init; }
    public required string SourceText { get; init; }
    public required StatementSyntax Statement { get; init; }
    public required TextSpan Span { get; init; }

    /// <summary>
    /// The 1-based line number of the statement, if the source text can be mapped.
    /// </summary>
    public int? Line { get; init; }

    /// <summary>
    /// The text of the statement extracted from the source.
    /// </summary>
    public string? StatementText { get; init; }
}

/// <summary>
/// Actions the debug hook can request after inspecting a statement.
/// </summary>
public enum DebugAction
{
    /// <summary>Continue execution normally.</summary>
    Continue,

    /// <summary>Execute this statement and stop at the next one.</summary>
    StepNext,

    /// <summary>Abort script execution.</summary>
    Abort,
}

/// <summary>
/// Delegate invoked before each statement in a block during evaluation.
/// Returning <see cref="DebugAction.Abort"/> causes a <see cref="DebugAbortException"/> to be thrown.
/// </summary>
public delegate Task<DebugAction> DebugHookDelegate(DebugStepContext context);
