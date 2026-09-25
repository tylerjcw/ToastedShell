namespace Tosh.Runtime;

/// <summary>
/// Protocol for shell objects (such as symbolic CAS expressions) that support
/// evaluation through standard mathematical functions like Math.sin, Math.cos, Math.sqrt, etc.
/// </summary>
public interface IShellMathFunctionObject
{
    /// <summary>
    /// Attempts to evaluate a named mathematical function with this object as its primary argument.
    /// </summary>
    bool TryEvaluateMathFunction(
        string functionName,
        IReadOnlyList<object?> additionalArguments,
        out object? result);
}
