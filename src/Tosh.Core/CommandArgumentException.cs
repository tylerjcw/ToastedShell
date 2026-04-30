namespace Tosh.Core;

/// <summary>
/// Thrown by built-in commands to signal that a specific argument is the
/// source of the failure. The engine catches this in <c>CreateCommandDiagnostic</c>
/// and narrows the diagnostic span to the offending argument so the rendered
/// underline points exactly at it instead of underlining the whole command.
/// </summary>
public sealed class CommandArgumentException : Exception
{
    public CommandArgumentException(int argumentIndex, string message)
        : base(message)
    {
        ArgumentIndex = argumentIndex;
    }

    /// <summary>0-based index into the command's argument list.</summary>
    public int ArgumentIndex { get; }
}
