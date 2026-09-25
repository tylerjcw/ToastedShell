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
        new SymEquation(Left.Differentiate(variable), Right.Differentiate(variable));

    public override SymExpr Substitute(string variable, SymExpr replacement) =>
        new SymEquation(Left.Substitute(variable, replacement), Right.Substitute(variable, replacement));

    public override double Evaluate(IReadOnlyDictionary<string, double>? context = null) =>
        Math.Abs(Left.Evaluate(context) - Right.Evaluate(context)) < 1e-12 ? 1.0 : 0.0;

    public override IEnumerable<string> GetVariables() =>
        Left.GetVariables().Concat(Right.GetVariables()).Distinct();

    public override bool Equals(SymExpr? other) =>
        other is SymEquation eq && Left.Equals(eq.Left) && Right.Equals(eq.Right);

    public override int GetHashCode() => HashCode.Combine(Left, Right);

    public override string ToString() => $"{Left} = {Right}";
}
