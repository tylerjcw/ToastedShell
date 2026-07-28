namespace Tosh.Runtime;

/// <summary>
/// Synchronous compatibility protocol for shell objects that participate in
/// ToastScript binary-operator dispatch. Interactive execution may provide a
/// cancellation-aware path in addition to this CLR/compiler boundary.
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
