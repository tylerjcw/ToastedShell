namespace Tosh.Runtime;

/// <summary>
/// Represents the outcome of a command that primarily performs side effects.
/// Commands should return a derived type with additional context when possible.
/// </summary>
public interface ICommandResult
{
    bool IsSuccess { get; }
    string Message { get; }
}

/// <summary>
/// A successful command outcome with an optional message.
/// </summary>
public record CommandSuccess(string Message = "OK") : ICommandResult
{
    public bool IsSuccess => true;
}

/// <summary>
/// A failed command outcome with a reason.
/// </summary>
public record CommandFailure(string Message) : ICommandResult
{
    public bool IsSuccess => false;
}

/// <summary>
/// Result of a file operation (touch, cp, mv, chmod, etc.)
/// </summary>
public record FileOperationResult(string Operation, string Path, bool IsSuccess, string Message) : ICommandResult;

/// <summary>
/// Result of a variable/environment mutation (export, unset, etc.)
/// </summary>
public record VariableOperationResult(string Operation, string Name, bool IsSuccess, string Message) : ICommandResult;
