using System.Numerics;

namespace Tosh.Stdlib.Cas;

public static class Simplifier
{
    public static SymExpr Add(SymExpr a, SymExpr b) => Sum([a, b]);

    public static SymExpr Subtract(SymExpr a, SymExpr b) => Add(a, Negate(b));

    public static SymExpr Negate(SymExpr a)
    {
        if (a is SymNumber num)
        {
            return new SymNumber(-num.Value);
        }
        return Multiply(new SymNumber(-1), a);
    }

    public static SymExpr Multiply(SymExpr a, SymExpr b) => Product([a, b]);

    public static SymExpr Divide(SymExpr a, SymExpr b)
    {
        if (b is SymNumber num && num.Value.IsZero)
            throw new DivideByZeroException("Symbolic division by zero.");

        if (a is SymNumber numA && numA.Value.IsZero)
            return SymExpr.Zero;

        if (b is SymNumber numB && numB.Value == BigRational.One)
            return a;

        return Multiply(a, Power(b, new SymNumber(-1)));
    }

    public static SymExpr Power(SymExpr @base, SymExpr exponent)
    {
        if (exponent is SymNumber expNum)
        {
            if (expNum.Value.IsZero) return SymExpr.One;
            if (expNum.Value == BigRational.One) return @base;
        }

        if (@base is SymNumber baseNum)
        {
            if (baseNum.Value.IsZero) return SymExpr.Zero;
            if (baseNum.Value == BigRational.One) return SymExpr.One;

            if (exponent is SymNumber expN && expN.Value.IsInteger)
            {
                var p = (int)expN.Value.Numerator;
                if (Math.Abs(p) < 100)
                {
                    return new SymNumber(baseNum.Value.Pow(p));
                }
            }
        }

        // (x^a)^b -> x^(a*b)
        if (@base is SymPow innerPow)
        {
            return Power(innerPow.Base, Multiply(innerPow.Exponent, exponent));
        }

        return new SymPow(@base, exponent);
    }

    public static SymExpr Sum(IEnumerable<SymExpr> rawTerms)
    {
        var flattened = new List<SymExpr>();
        BigRational constantSum = BigRational.Zero;

        void Flatten(SymExpr expr)
        {
            if (expr is SymAdd add)
            {
                foreach (var term in add.Terms) Flatten(term);
            }
            else if (expr is SymNumber num)
            {
                constantSum += num.Value;
            }
            else
            {
                flattened.Add(expr);
            }
        }

        foreach (var t in rawTerms) Flatten(t);

        // Group like terms: each term has a coefficient and a kernel (e.g. 3 * x^2 has coeff 3, kernel x^2)
        var grouped = new Dictionary<SymExpr, BigRational>();
        foreach (var term in flattened)
        {
            var (coeff, kernel) = DecomposeTerm(term);
            if (grouped.ContainsKey(kernel))
                grouped[kernel] += coeff;
            else
                grouped[kernel] = coeff;
        }

        var resultTerms = new List<SymExpr>();
        if (!constantSum.IsZero)
        {
            resultTerms.Add(new SymNumber(constantSum));
        }

        foreach (var (kernel, coeff) in grouped)
        {
            if (coeff.IsZero) continue;
            if (coeff == BigRational.One)
            {
                resultTerms.Add(kernel);
            }
            else
            {
                resultTerms.Add(Product([new SymNumber(coeff), kernel]));
            }
        }

        if (resultTerms.Count == 0) return SymExpr.Zero;
        if (resultTerms.Count == 1) return resultTerms[0];

        // Sort terms for canonical representation
        resultTerms.Sort((x, y) => string.CompareOrdinal(x.ToString(), y.ToString()));
        return new SymAdd(resultTerms);
    }

    public static SymExpr Product(IEnumerable<SymExpr> rawFactors)
    {
        var flattened = new List<SymExpr>();
        BigRational constantProd = BigRational.One;

        void Flatten(SymExpr expr)
        {
            if (expr is SymMul mul)
            {
                foreach (var f in mul.Factors) Flatten(f);
            }
            else if (expr is SymNumber num)
            {
                constantProd *= num.Value;
            }
            else
            {
                flattened.Add(expr);
            }
        }

        foreach (var f in rawFactors) Flatten(f);

        if (constantProd.IsZero) return SymExpr.Zero;

        // Group factors by base: e.g. x * x^2 -> x^3
        var grouped = new Dictionary<SymExpr, SymExpr>();
        foreach (var factor in flattened)
        {
            var (baseExpr, expExpr) = DecomposeFactor(factor);
            if (grouped.ContainsKey(baseExpr))
                grouped[baseExpr] = Add(grouped[baseExpr], expExpr);
            else
                grouped[baseExpr] = expExpr;
        }

        var resultFactors = new List<SymExpr>();
        if (constantProd != BigRational.One || grouped.Count == 0)
        {
            resultFactors.Add(new SymNumber(constantProd));
        }

        foreach (var (baseExpr, expExpr) in grouped)
        {
            var p = Power(baseExpr, expExpr);
            if (p is SymNumber num && num.Value == BigRational.One) continue;
            resultFactors.Add(p);
        }

        if (resultFactors.Count == 0) return SymExpr.One;
        if (resultFactors.Count == 1) return resultFactors[0];

        return new SymMul(resultFactors);
    }

    private static (BigRational Coeff, SymExpr Kernel) DecomposeTerm(SymExpr expr)
    {
        if (expr is SymMul mul && mul.Factors.Count > 0 && mul.Factors[0] is SymNumber num)
        {
            var remaining = mul.Factors.Skip(1).ToList();
            var kernel = remaining.Count == 1 ? remaining[0] : new SymMul(remaining);
            return (num.Value, kernel);
        }
        return (BigRational.One, expr);
    }

    private static (SymExpr Base, SymExpr Exponent) DecomposeFactor(SymExpr expr)
    {
        if (expr is SymPow pow)
        {
            return (pow.Base, pow.Exponent);
        }
        return (expr, SymExpr.One);
    }

    public static SymExpr Simplify(SymExpr expr)
    {
        return expr switch
        {
            SymEquation eq => new SymEquation(Simplify(eq.Left), Simplify(eq.Right)),
            SymAdd add => Sum(add.Terms.Select(Simplify)),
            SymMul mul => Product(mul.Factors.Select(Simplify)),
            SymPow pow => Power(Simplify(pow.Base), Simplify(pow.Exponent)),
            SymFunction fn => SimplifyFunction(fn),
            _ => expr
        };
    }

    private static SymExpr SimplifyFunction(SymFunction fn)
    {
        var simplifiedArgs = fn.Arguments.Select(Simplify).ToList();
        if (simplifiedArgs.Count == 1 && simplifiedArgs[0] is SymNumber num)
        {
            // Evaluate well-known special values
            if (fn.Name == "sin" && num.Value.IsZero) return SymExpr.Zero;
            if (fn.Name == "cos" && num.Value.IsZero) return SymExpr.One;
            if (fn.Name == "tan" && num.Value.IsZero) return SymExpr.Zero;
            if (fn.Name == "exp" && num.Value.IsZero) return SymExpr.One;
            if (fn.Name == "ln" && num.Value == BigRational.One) return SymExpr.Zero;
            if (fn.Name == "sqrt" && num.Value.IsZero) return SymExpr.Zero;
            if (fn.Name == "sqrt" && num.Value == BigRational.One) return SymExpr.One;
        }

        return new SymFunction(fn.Name, simplifiedArgs);
    }
}
