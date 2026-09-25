namespace Tosh.Stdlib.Cas;

public static class Expander
{
    public static SymExpr Expand(SymExpr expr)
    {
        return expr switch
        {
            SymEquation eq => new SymEquation(Expand(eq.Left), Expand(eq.Right)),
            SymAdd add => Simplifier.Sum(add.Terms.Select(Expand)),
            SymMul mul => ExpandProduct(mul.Factors.Select(Expand).ToList()),
            SymPow pow => ExpandPower(pow),
            SymFunction fn => new SymFunction(fn.Name, fn.Arguments.Select(Expand).ToList()),
            _ => expr
        };
    }

    private static SymExpr ExpandPower(SymPow pow)
    {
        var expandedBase = Expand(pow.Base);
        if (pow.Exponent is SymNumber num && num.Value.IsInteger && num.Value.Numerator > 0 && num.Value.Numerator <= 20)
        {
            var exp = (int)num.Value.Numerator;
            SymExpr result = SymExpr.One;
            for (int i = 0; i < exp; i++)
            {
                result = ExpandMultiply(result, expandedBase);
            }
            return result;
        }

        return Simplifier.Power(expandedBase, pow.Exponent);
    }

    private static SymExpr ExpandProduct(IReadOnlyList<SymExpr> factors)
    {
        if (factors.Count == 0) return SymExpr.One;
        SymExpr current = factors[0];
        for (int i = 1; i < factors.Count; i++)
        {
            current = ExpandMultiply(current, factors[i]);
        }
        return current;
    }

    public static SymExpr ExpandMultiply(SymExpr a, SymExpr b)
    {
        // Distribute over addition: (A1 + A2 + ...) * (B1 + B2 + ...) = Sum(Ai * Bj)
        var termsA = (a is SymAdd addA) ? addA.Terms : [a];
        var termsB = (b is SymAdd addB) ? addB.Terms : [b];

        var crossTerms = new List<SymExpr>(termsA.Count * termsB.Count);
        foreach (var ta in termsA)
        {
            foreach (var tb in termsB)
            {
                crossTerms.Add(Simplifier.Multiply(ta, tb));
            }
        }

        return Simplifier.Sum(crossTerms);
    }
}
