namespace Tosh.Stdlib.Cas;

public sealed class SymEquation : SymExpr
{
    public SymExpr Left { get; }
    public SymExpr Right { get; }

    public SymEquation(SymExpr left, SymExpr right)
    {
        Left = left;
        Right = right;
    }

    public SymExpr ToStandardForm() => Simplifier.Subtract(Left, Right);

    public override int Precedence => 0;

    public override SymExpr Differentiate(string variable) =>
        new SymEquation(Simplifier.Simplify(Left.Differentiate(variable)), Simplifier.Simplify(Right.Differentiate(variable)));

    public override SymExpr Substitute(string variable, SymExpr replacement) =>
        new SymEquation(Left.Substitute(variable, replacement), Right.Substitute(variable, replacement));

    public override double Evaluate(IReadOnlyDictionary<string, double>? context = null) =>
        Math.Abs(Left.Evaluate(context) - Right.Evaluate(context)) < 1e-12 ? 1.0 : 0.0;

    public override IEnumerable<string> GetVariables() =>
        Left.GetVariables().Concat(Right.GetVariables()).Distinct();

    public override bool TryEvaluateBinaryOperator(
        string operatorName,
        object? other,
        bool reversed,
        out object? value)
    {
        value = null;
        if (!TryCoerce(other, out var otherExpr))
        {
            return false;
        }

        if (operatorName is "==" or "=")
        {
            value = new SymEquation(this, otherExpr);
            return true;
        }

        // Another equation: (L1 == R1) OP (L2 == R2) -> (L1 OP L2) == (R1 OP R2)
        if (otherExpr is SymEquation eqOther)
        {
            var leftSide = reversed
                ? EvaluateBinaryDirect(operatorName, eqOther.Left, Left)
                : EvaluateBinaryDirect(operatorName, Left, eqOther.Left);
            var rightSide = reversed
                ? EvaluateBinaryDirect(operatorName, eqOther.Right, Right)
                : EvaluateBinaryDirect(operatorName, Right, eqOther.Right);

            if (leftSide is not null && rightSide is not null)
            {
                value = new SymEquation(Simplifier.Simplify(leftSide), Simplifier.Simplify(rightSide));
                return true;
            }
            return false;
        }

        // Scalar or expression: distribute to both sides of the equation
        var newLeft = reversed
            ? EvaluateBinaryDirect(operatorName, otherExpr, Left)
            : EvaluateBinaryDirect(operatorName, Left, otherExpr);
        var newRight = reversed
            ? EvaluateBinaryDirect(operatorName, otherExpr, Right)
            : EvaluateBinaryDirect(operatorName, Right, otherExpr);

        if (newLeft is not null && newRight is not null)
        {
            value = new SymEquation(Simplifier.Simplify(newLeft), Simplifier.Simplify(newRight));
            return true;
        }

        return false;
    }

    public static SymExpr? EvaluateBinaryDirect(string op, SymExpr a, SymExpr b) => op switch
    {
        "+" => Simplifier.Add(a, b),
        "-" => Simplifier.Subtract(a, b),
        "*" => Simplifier.Multiply(a, b),
        "/" => Simplifier.Divide(a, b),
        "**" or "^" => Simplifier.Power(a, b),
        _ => null
    };

    public override bool TryEvaluateMathFunction(
        string functionName,
        IReadOnlyList<object?> additionalArguments,
        out object? result)
    {
        if (Left.TryEvaluateMathFunction(functionName, additionalArguments, out var newLeft) &&
            Right.TryEvaluateMathFunction(functionName, additionalArguments, out var newRight) &&
            newLeft is SymExpr lExpr && newRight is SymExpr rExpr)
        {
            result = new SymEquation(Simplifier.Simplify(lExpr), Simplifier.Simplify(rExpr));
            return true;
        }

        result = null;
        return false;
    }

    public override bool Equals(SymExpr? other) =>
        other is SymEquation eq && Left.Equals(eq.Left) && Right.Equals(eq.Right);

    public override int GetHashCode() => HashCode.Combine(Left, Right);

    public override string ToString() => $"{Left} = {Right}";
}
