namespace Tosh.Runtime;

/// <summary>
/// Synchronous compatibility protocol for shell objects that participate in
/// ToastScript binary-operator dispatch.
/// </summary>
public interface IShellBinaryOperatorObject
{
    /// <summary>
    /// Attempts to evaluate <paramref name="operatorName"/> with this object
    /// as the dispatch receiver and <paramref name="other"/> as its operand.
    /// </summary>
    bool TryEvaluateBinaryOperator(
        string operatorName,
        object? other,
        out object? value);
}

/// <summary>
/// Extension protocol for shell objects that participate in binary-operator
/// dispatch with awareness of operand position (e.g. for non-commutative operations
/// like subtraction and division where the object is on the right-hand side).
/// </summary>
public interface IShellReversibleBinaryOperatorObject
{
    /// <summary>
    /// Attempts to evaluate <paramref name="operatorName"/> with this object
    /// as the receiver and <paramref name="other"/> as the other operand.
    /// </summary>
    /// <param name="operatorName">The operator symbol (+, -, *, /, ==, etc.)</param>
    /// <param name="other">The other operand.</param>
    /// <param name="reversed">True when this object is the right-hand operand (other OP this).</param>
    /// <param name="value">The resulting value if handled.</param>
    bool TryEvaluateBinaryOperator(
        string operatorName,
        object? other,
        bool reversed,
        out object? value);
}
