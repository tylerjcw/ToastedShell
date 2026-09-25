using Tosh.Runtime;

namespace Tosh.Language.Binding;

/// <summary>
/// Constant-folds operator expressions whose operands are literals. Runs as part of the
/// lowering pass and stamps the result onto the parse tree's
/// <see cref="Parsing.OperatorArgumentSyntax.FoldedConstant"/> /
/// <see cref="Parsing.UnaryOperatorArgumentSyntax.FoldedConstant"/> side-tables so the
/// evaluator can short-circuit without changing its public seam.
/// </summary>
/// <remarks>
/// <para>
/// The fold is computed by <see cref="OperatorEvaluator"/> — the evaluator the engine itself
/// runs for these operands — so a folded answer is the evaluated answer by construction. It
/// used to carry its own numeric tower, and that copy drifted three times: comparisons
/// through a lossy <c>decimal</c> (<c>0.3 == 0.30000000000000004</c> folded true), a decimal
/// conversion that differed from <c>OperatorEvaluator.ToDecimal</c>, and division when
/// <c>int / int</c> became true division. Each was a program meaning something different
/// depending on whether an expression happened to be foldable.
/// </para>
/// <para>
/// Conservative about <em>what</em> it folds: literal strings, booleans and the four literal
/// numeric types, under the operators listed below. Anything the evaluator refuses —
/// division by zero, an incompatible pair — is left unfolded, so the evaluator reports it at
/// run time with its source position.
/// </para>
/// </remarks>
public static class ConstantFolder
{
    private static readonly HashSet<string> NumericOperators = new(StringComparer.Ordinal)
    {
        "+", "-", "*", "/", "%", "==", "!=", "<", "<=", ">", ">=",
    };

    public static object? TryFoldBinary(BoundExpression left, string op, BoundExpression right)
    {
        if (left is not BoundLiteral l || right is not BoundLiteral r) return Sentinel.NoFold;
        return EvaluateBinary(l.Value, op, r.Value);
    }

    public static object? TryFoldUnary(string op, BoundExpression operand)
    {
        if (operand is not BoundLiteral l) return Sentinel.NoFold;
        return EvaluateUnary(op, l.Value);
    }

    /// <summary>
    /// Returned by the Try* methods to distinguish "folded to null"
    /// (a legitimate null result) from "unable to fold" (no-op).
    /// </summary>
    public static class Sentinel
    {
        public static readonly object NoFold = new();
    }

    private static object? EvaluateBinary(object? left, string op, object? right)
    {
        var canonical = op switch
        {
            "&&" => "and",
            "||" => "or",
            _ => op,
        };

        var foldable = (left, right) switch
        {
            (string, string) => canonical == "+",
            (bool, bool) => canonical is "and" or "or" or "==" or "!=",
            _ => IsNumeric(left) && IsNumeric(right) && NumericOperators.Contains(canonical),
        };

        return foldable
            ? Evaluate(() => OperatorEvaluator.EvaluateBinary(left, canonical, right))
            : Sentinel.NoFold;
    }

    private static object? EvaluateUnary(string op, object? operand)
    {
        var foldable = op switch
        {
            "-" or "+" => IsNumeric(operand),
            "!" or "not" => operand is bool,
            _ => false,
        };

        return foldable
            ? Evaluate(() => OperatorEvaluator.EvaluateUnary(op, operand))
            : Sentinel.NoFold;
    }

    /// <summary>
    /// Runs one evaluation, declining the fold on any failure: a refusal is the evaluator's
    /// to report, at run time, where the reader can see which expression caused it.
    /// </summary>
    private static object? Evaluate(Func<object?> evaluation)
    {
        try
        {
            return evaluation();
        }
        catch (Exception)
        {
            return Sentinel.NoFold;
        }
    }

    private static bool IsNumeric(object? value) =>
        value is int or long or double or decimal;
}
