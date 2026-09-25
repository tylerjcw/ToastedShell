using System.Numerics;

namespace Tosh.Stdlib.Cas;

public static class Solver
{
    public static IReadOnlyList<SymExpr> Solve(string equation, string variable = "x")
    {
        SymExpr expr;
        if (equation.Contains("=="))
        {
            var parts = equation.Split("==");
            var lhs = SymParser.Parse(parts[0]);
            var rhs = SymParser.Parse(parts[1]);
            expr = Simplifier.Subtract(lhs, rhs);
        }
        else if (equation.Contains('='))
        {
            var parts = equation.Split('=');
            var lhs = SymParser.Parse(parts[0]);
            var rhs = SymParser.Parse(parts[1]);
            expr = Simplifier.Subtract(lhs, rhs);
        }
        else
        {
            expr = SymParser.Parse(equation);
        }

        return Solve(expr, variable);
    }

    public static IReadOnlyList<SymExpr> Solve(SymExpr expression, string variable = "x")
    {
        var expanded = Expander.Expand(expression);
        var simplified = Simplifier.Simplify(expanded);

        var coeffs = GetPolynomialCoefficients(simplified, variable);

        int maxDegree = coeffs.Keys.DefaultIfEmpty(0).Max();

        if (maxDegree == 0)
        {
            if (coeffs.TryGetValue(0, out var c0) && c0.IsZero)
            {
                return [new SymVariable(variable)];
            }
            return Array.Empty<SymExpr>();
        }

        if (maxDegree == 1)
        {
            var c1 = coeffs.GetValueOrDefault(1, SymExpr.Zero);
            var c0 = coeffs.GetValueOrDefault(0, SymExpr.Zero);
            var sol = Simplifier.Simplify(Simplifier.Divide(Simplifier.Negate(c0), c1));
            return [sol];
        }

        if (maxDegree == 2)
        {
            var a = coeffs.GetValueOrDefault(2, SymExpr.Zero);
            var b = coeffs.GetValueOrDefault(1, SymExpr.Zero);
            var c = coeffs.GetValueOrDefault(0, SymExpr.Zero);

            var b2 = Simplifier.Power(b, 2);
            var ac4 = Simplifier.Multiply(4, Simplifier.Multiply(a, c));
            var disc = Simplifier.Simplify(Simplifier.Subtract(b2, ac4));

            SymExpr sqrtDisc;
            if (disc is SymNumber num && num.Value.Sign >= 0)
            {
                var dVal = num.Value;
                var numSqrt = SqrtExact(dVal.Numerator);
                var denSqrt = SqrtExact(dVal.Denominator);
                if (numSqrt.HasValue && denSqrt.HasValue)
                {
                    sqrtDisc = new SymNumber(new BigRational(numSqrt.Value, denSqrt.Value));
                }
                else
                {
                    sqrtDisc = new SymFunction("sqrt", [disc]);
                }
            }
            else
            {
                sqrtDisc = new SymFunction("sqrt", [disc]);
            }

            var twoA = Simplifier.Multiply(2, a);
            var negB = Simplifier.Negate(b);

            if (disc.IsZero)
            {
                var r = Simplifier.Simplify(Simplifier.Divide(negB, twoA));
                return [r];
            }

            var root1 = Simplifier.Simplify(Simplifier.Divide(Simplifier.Add(negB, sqrtDisc), twoA));
            var root2 = Simplifier.Simplify(Simplifier.Divide(Simplifier.Subtract(negB, sqrtDisc), twoA));

            return [root1, root2];
        }

        if (coeffs.Count <= 2 && coeffs.ContainsKey(maxDegree))
        {
            var a = coeffs[maxDegree];
            var c = coeffs.GetValueOrDefault(0, SymExpr.Zero);
            var rhs = Simplifier.Simplify(Simplifier.Divide(Simplifier.Negate(c), a));
            var sol = Simplifier.Simplify(Simplifier.Power(rhs, new BigRational(1, maxDegree)));
            return [sol];
        }

        throw new NotSupportedException($"Solving polynomial equations of degree {maxDegree} is not supported.");
    }

    private static Dictionary<int, SymExpr> GetPolynomialCoefficients(SymExpr expr, string variable)
    {
        var coeffs = new Dictionary<int, SymExpr>();
        var terms = (expr is SymAdd add) ? add.Terms : [expr];

        foreach (var term in terms)
        {
            var (deg, coeff) = ExtractDegreeAndCoeff(term, variable);
            if (coeffs.ContainsKey(deg))
            {
                coeffs[deg] = Simplifier.Add(coeffs[deg], coeff);
            }
            else
            {
                coeffs[deg] = coeff;
            }
        }

        var result = new Dictionary<int, SymExpr>();
        foreach (var (deg, c) in coeffs)
        {
            var simp = Simplifier.Simplify(c);
            if (!simp.IsZero)
            {
                result[deg] = simp;
            }
        }

        return result;
    }

    private static (int Degree, SymExpr Coeff) ExtractDegreeAndCoeff(SymExpr term, string variable)
    {
        if (term is SymVariable v && v.Name == variable)
        {
            return (1, SymExpr.One);
        }

        if (term is SymPow pow && pow.Base is SymVariable pv && pv.Name == variable)
        {
            if (pow.Exponent is SymNumber expNum && expNum.Value.IsInteger)
            {
                return ((int)expNum.Value.Numerator, SymExpr.One);
            }
        }

        if (term is SymMul mul)
        {
            int deg = 0;
            var otherFactors = new List<SymExpr>();
            foreach (var f in mul.Factors)
            {
                if (f is SymVariable fv && fv.Name == variable)
                {
                    deg += 1;
                }
                else if (f is SymPow fp && fp.Base is SymVariable fpv && fpv.Name == variable &&
                         fp.Exponent is SymNumber fexp && fexp.Value.IsInteger)
                {
                    deg += (int)fexp.Value.Numerator;
                }
                else
                {
                    otherFactors.Add(f);
                }
            }

            if (deg > 0)
            {
                var coeff = otherFactors.Count == 0 ? SymExpr.One :
                            otherFactors.Count == 1 ? otherFactors[0] : new SymMul(otherFactors);
                return (deg, coeff);
            }
        }

        return (0, term);
    }

    private static BigInteger? SqrtExact(BigInteger n)
    {
        if (n < 0) return null;
        if (n == 0) return 0;
        if (n == 1) return 1;

        BigInteger x0 = n / 2;
        if (x0 != 0)
        {
            BigInteger x1 = (x0 + n / x0) / 2;
            while (x1 < x0)
            {
                x0 = x1;
                x1 = (x0 + n / x0) / 2;
            }
            if (x0 * x0 == n) return x0;
        }
        return null;
    }
}
