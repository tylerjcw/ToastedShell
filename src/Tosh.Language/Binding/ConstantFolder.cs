using Tosh.Compiler.IR;
using Tosh.Language.Parsing;

namespace Tosh.Language.Binding;

/// <summary>
/// Constant-folds operator expressions whose operands are
/// statically-known literals. Runs as part of the lowering pass and
/// stamps the result onto the parse tree's
/// <see cref="OperatorArgumentSyntax.FoldedConstant"/> /
/// <see cref="UnaryOperatorArgumentSyntax.FoldedConstant"/>
/// side-tables so the existing evaluator can short-circuit without
/// changing its public seam.
///
/// Conservative: anything that could trap at runtime (division by
/// zero, overflow, mixed-type exotic conversions) is left for the
/// evaluator to handle. Anything we can't *cheaply* prove safe stays
/// unfolded.
/// </summary>
public static class ConstantFolder
{
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
        // String concatenation is the only string-on-string fold we do.
        if (op == "+" && left is string ls && right is string rs) return ls + rs;

        // Boolean short-circuits with both sides known.
        if (left is bool lb && right is bool rb)
        {
            return op switch
            {
                "&&" or "and" => lb && rb,
                "||" or "or" => lb || rb,
                "==" => lb == rb,
                "!=" => lb != rb,
                _ => Sentinel.NoFold,
            };
        }

        if (!IsNumeric(left) || !IsNumeric(right)) return Sentinel.NoFold;

        return op switch
        {
            "+" => NumericAdd(left, right),
            "-" => NumericSub(left, right),
            "*" => NumericMul(left, right),
            "/" => NumericDiv(left, right),
            "%" => NumericMod(left, right),
            "==" => NumericEq(left, right),
            "!=" => !(bool)NumericEq(left, right)!,
            "<" => NumericCmp(left, right) < 0,
            "<=" => NumericCmp(left, right) <= 0,
            ">" => NumericCmp(left, right) > 0,
            ">=" => NumericCmp(left, right) >= 0,
            _ => Sentinel.NoFold,
        };
    }

    private static object? EvaluateUnary(string op, object? operand) => op switch
    {
        "-" when operand is int i => -i,
        "-" when operand is long l => -l,
        "-" when operand is double d => -d,
        "-" when operand is decimal m => -m,
        "+" when IsNumeric(operand) => operand,
        "!" or "not" when operand is bool b => !b,
        _ => Sentinel.NoFold,
    };

    private static bool IsNumeric(object? value) =>
        value is int or long or double or decimal;

    // Numeric promotion follows the C# rules we apply in TypeInferrer.
    // We avoid checked() so overflow falls back to the evaluator.

    private static object? NumericAdd(object? a, object? b)
    {
        try { return ToDecimalIfNeeded(a, b, (x, y) => checked(x + y), (x, y) => checked(x + y), (x, y) => x + y, (x, y) => x + y); }
        catch (OverflowException) { return Sentinel.NoFold; }
    }

    private static object? NumericSub(object? a, object? b)
    {
        try { return ToDecimalIfNeeded(a, b, (x, y) => checked(x - y), (x, y) => checked(x - y), (x, y) => x - y, (x, y) => x - y); }
        catch (OverflowException) { return Sentinel.NoFold; }
    }

    private static object? NumericMul(object? a, object? b)
    {
        try { return ToDecimalIfNeeded(a, b, (x, y) => checked(x * y), (x, y) => checked(x * y), (x, y) => x * y, (x, y) => x * y); }
        catch (OverflowException) { return Sentinel.NoFold; }
    }

    private static object? NumericDiv(object? a, object? b)
    {
        // Integer division by zero is a runtime concern; defer.
        if ((b is int bi && bi == 0) || (b is long bl && bl == 0) || (b is decimal bm && bm == 0m))
            return Sentinel.NoFold;
        try { return ToDecimalIfNeeded(a, b, (x, y) => x / y, (x, y) => x / y, (x, y) => x / y, (x, y) => x / y); }
        catch (DivideByZeroException) { return Sentinel.NoFold; }
        catch (OverflowException) { return Sentinel.NoFold; }
    }

    private static object? NumericMod(object? a, object? b)
    {
        if ((b is int bi && bi == 0) || (b is long bl && bl == 0) || (b is decimal bm && bm == 0m))
            return Sentinel.NoFold;
        try { return ToDecimalIfNeeded(a, b, (x, y) => x % y, (x, y) => x % y, (x, y) => x % y, (x, y) => x % y); }
        catch (DivideByZeroException) { return Sentinel.NoFold; }
        catch (OverflowException) { return Sentinel.NoFold; }
    }

    private static object NumericEq(object? a, object? b) =>
        ToDecimalCompare(a!, b!) == 0;

    private static int NumericCmp(object? a, object? b) =>
        ToDecimalCompare(a!, b!);

    private static int ToDecimalCompare(object a, object b)
    {
        // decimal preserves int and long exactly; promotes double via
        // its decimal converter. This matches the runtime's comparison
        // semantics for numeric types we model.
        var da = Convert.ToDecimal(a, System.Globalization.CultureInfo.InvariantCulture);
        var db = Convert.ToDecimal(b, System.Globalization.CultureInfo.InvariantCulture);
        return da.CompareTo(db);
    }

    /// <summary>
    /// Ladder of numeric op evaluators. We prefer the smallest CLR
    /// type that holds both operands so the resulting literal looks
    /// like the runtime would produce it.
    /// </summary>
    private static object ToDecimalIfNeeded(
        object? a,
        object? b,
        Func<int, int, int> intOp,
        Func<long, long, long> longOp,
        Func<double, double, double> doubleOp,
        Func<decimal, decimal, decimal> decimalOp)
    {
        if (a is decimal || b is decimal)
            return decimalOp(Convert.ToDecimal(a!), Convert.ToDecimal(b!));
        if (a is double || b is double)
            return doubleOp(Convert.ToDouble(a!), Convert.ToDouble(b!));
        if (a is long || b is long)
            return longOp(Convert.ToInt64(a!), Convert.ToInt64(b!));
        return intOp((int)a!, (int)b!);
    }
}
