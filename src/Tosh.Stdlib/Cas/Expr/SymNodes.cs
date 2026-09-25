using System.Globalization;

namespace Tosh.Stdlib.Cas;

public sealed class SymNumber : SymExpr
{
    public BigRational Value { get; }
    public override int Precedence => 100;
    public override bool IsZero => Value.IsZero;
    public override bool IsOne => Value.IsOne;
    public override bool IsNumber => true;

    public static new readonly SymNumber Zero = new(BigRational.Zero);
    public static new readonly SymNumber One = new(BigRational.One);

    public int Sign => Value.Sign;
    public System.Numerics.BigInteger Numerator => Value.Numerator;
    public System.Numerics.BigInteger Denominator => Value.Denominator;
    public override double Approximate => Value.ToDouble();

    public SymNumber(BigRational value) => Value = value;

    public override SymExpr Differentiate(string variable) => Zero;
    public override SymExpr Substitute(string variable, SymExpr replacement) => this;
    public override double Evaluate(IReadOnlyDictionary<string, double>? context = null) => Value.ToDouble();

    public override bool Equals(SymExpr? other) => other is SymNumber num && Value.Equals(num.Value);
    public override int GetHashCode() => Value.GetHashCode();
}

public sealed class SymVariable : SymExpr
{
    public string Name { get; }
    public override int Precedence => 100;

    public SymVariable(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name.Trim();
    }

    public override SymExpr Differentiate(string variable) =>
        string.Equals(Name, variable, StringComparison.Ordinal) ? One : Zero;

    public override SymExpr Substitute(string variable, SymExpr replacement) =>
        string.Equals(Name, variable, StringComparison.Ordinal) ? replacement : this;

    public override double Evaluate(IReadOnlyDictionary<string, double>? context = null)
    {
        if (context is not null && context.TryGetValue(Name, out var val))
        {
            return val;
        }
        throw new InvalidOperationException($"Variable '{Name}' is not defined in evaluation context.");
    }

    public override IEnumerable<string> GetVariables() => [Name];

    public override bool Equals(SymExpr? other) => other is SymVariable v && string.Equals(Name, v.Name, StringComparison.Ordinal);
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Name);
}

public sealed class SymConstant : SymExpr
{
    public string Name { get; }
    public double NumericalValue { get; }
    public override int Precedence => 100;

    public SymConstant(string name, double numericalValue)
    {
        Name = name;
        NumericalValue = numericalValue;
    }

    public override SymExpr Differentiate(string variable) => Zero;
    public override SymExpr Substitute(string variable, SymExpr replacement) => this;
    public override double Evaluate(IReadOnlyDictionary<string, double>? context = null) => NumericalValue;

    public override bool Equals(SymExpr? other) => other is SymConstant c && string.Equals(Name, c.Name, StringComparison.Ordinal);
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Name);
}

public sealed class SymAdd : SymExpr
{
    public IReadOnlyList<SymExpr> Terms { get; }
    public override int Precedence => 10;

    public SymAdd(IReadOnlyList<SymExpr> terms) => Terms = terms;

    public override SymExpr Differentiate(string variable) =>
        Simplifier.Sum(Terms.Select(t => t.Differentiate(variable)));

    public override SymExpr Substitute(string variable, SymExpr replacement) =>
        Simplifier.Sum(Terms.Select(t => t.Substitute(variable, replacement)));

    public override double Evaluate(IReadOnlyDictionary<string, double>? context = null) =>
        Terms.Sum(t => t.Evaluate(context));

    public override IEnumerable<string> GetVariables() =>
        Terms.SelectMany(t => t.GetVariables()).Distinct();

    public override bool Equals(SymExpr? other) =>
        other is SymAdd add && Terms.Count == add.Terms.Count && Terms.SequenceEqual(add.Terms);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var t in Terms) hash.Add(t);
        return hash.ToHashCode();
    }
}

public sealed class SymMul : SymExpr
{
    public IReadOnlyList<SymExpr> Factors { get; }
    public override int Precedence => 20;

    public SymMul(IReadOnlyList<SymExpr> factors) => Factors = factors;

    public override SymExpr Differentiate(string variable)
    {
        // Product rule: d(f*g*h) = f'*g*h + f*g'*h + f*g*h'
        var sumTerms = new List<SymExpr>(Factors.Count);
        for (int i = 0; i < Factors.Count; i++)
        {
            var productFactors = new List<SymExpr>(Factors.Count);
            for (int j = 0; j < Factors.Count; j++)
            {
                productFactors.Add(i == j ? Factors[j].Differentiate(variable) : Factors[j]);
            }
            sumTerms.Add(Simplifier.Product(productFactors));
        }
        return Simplifier.Sum(sumTerms);
    }

    public override SymExpr Substitute(string variable, SymExpr replacement) =>
        Simplifier.Product(Factors.Select(f => f.Substitute(variable, replacement)));

    public override double Evaluate(IReadOnlyDictionary<string, double>? context = null)
    {
        double result = 1.0;
        foreach (var f in Factors) result *= f.Evaluate(context);
        return result;
    }

    public override IEnumerable<string> GetVariables() =>
        Factors.SelectMany(f => f.GetVariables()).Distinct();

    public override bool Equals(SymExpr? other) =>
        other is SymMul mul && Factors.Count == mul.Factors.Count && Factors.SequenceEqual(mul.Factors);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var f in Factors) hash.Add(f);
        return hash.ToHashCode();
    }
}

public sealed class SymPow : SymExpr
{
    public SymExpr Base { get; }
    public SymExpr Exponent { get; }
    public override int Precedence => 30;

    public SymPow(SymExpr @base, SymExpr exponent)
    {
        Base = @base;
        Exponent = exponent;
    }

    public override SymExpr Differentiate(string variable)
    {
        // General power rule: d(u^v)/dx = u^v * (v'*ln(u) + v*u'/u)
        // Common case: v is constant number n: n * u^(n-1) * u'
        if (Exponent is SymNumber num && num.Value.IsInteger)
        {
            var n = (int)num.Value.Numerator;
            var newPow = Simplifier.Power(Base, new SymNumber(n - 1));
            return Simplifier.Product([num, newPow, Base.Differentiate(variable)]);
        }

        // General chain rule with logarithm
        var term1 = Simplifier.Multiply(Exponent.Differentiate(variable), new SymFunction("ln", [Base]));
        var term2 = Simplifier.Multiply(Exponent, Simplifier.Divide(Base.Differentiate(variable), Base));
        return Simplifier.Multiply(this, Simplifier.Add(term1, term2));
    }

    public override SymExpr Substitute(string variable, SymExpr replacement) =>
        Simplifier.Power(Base.Substitute(variable, replacement), Exponent.Substitute(variable, replacement));

    public override double Evaluate(IReadOnlyDictionary<string, double>? context = null) =>
        Math.Pow(Base.Evaluate(context), Exponent.Evaluate(context));

    public override IEnumerable<string> GetVariables() =>
        Base.GetVariables().Concat(Exponent.GetVariables()).Distinct();

    public override bool Equals(SymExpr? other) =>
        other is SymPow pow && Base.Equals(pow.Base) && Exponent.Equals(pow.Exponent);

    public override int GetHashCode() => HashCode.Combine(Base, Exponent);
}

public sealed class SymFunction : SymExpr
{
    public string Name { get; }
    public IReadOnlyList<SymExpr> Arguments { get; }
    public override int Precedence => 100;

    public SymFunction(string name, IReadOnlyList<SymExpr> arguments)
    {
        Name = name.ToLowerInvariant();
        Arguments = arguments;
    }

    public override SymExpr Differentiate(string variable)
    {
        if (Arguments.Count != 1)
        {
            throw new NotSupportedException($"Differentiation of multi-argument function '{Name}' is not supported.");
        }

        var arg = Arguments[0];
        var argDiff = arg.Differentiate(variable);

        // Derivative table
        SymExpr outerDiff = Name switch
        {
            "sin" => new SymFunction("cos", [arg]),
            "cos" => Simplifier.Negate(new SymFunction("sin", [arg])),
            "tan" => Simplifier.Power(new SymFunction("cos", [arg]), -2),
            "exp" => this,
            "ln" => Simplifier.Divide(One, arg),
            "sqrt" => Simplifier.Divide(One, Simplifier.Multiply(2, new SymFunction("sqrt", [arg]))),
            "asin" => Simplifier.Divide(One, Simplifier.Power(Simplifier.Subtract(One, Simplifier.Power(arg, 2)), new BigRational(1, 2))),
            "acos" => Simplifier.Negate(Simplifier.Divide(One, Simplifier.Power(Simplifier.Subtract(One, Simplifier.Power(arg, 2)), new BigRational(1, 2)))),
            "atan" => Simplifier.Divide(One, Simplifier.Add(One, Simplifier.Power(arg, 2))),
            _ => throw new NotSupportedException($"Derivative of function '{Name}' is not defined.")
        };

        return Simplifier.Multiply(outerDiff, argDiff);
    }

    public override SymExpr Substitute(string variable, SymExpr replacement) =>
        new SymFunction(Name, Arguments.Select(a => a.Substitute(variable, replacement)).ToList());

    public override double Evaluate(IReadOnlyDictionary<string, double>? context = null)
    {
        var evaluatedArgs = Arguments.Select(a => a.Evaluate(context)).ToArray();
        return Name switch
        {
            "sin" => Math.Sin(evaluatedArgs[0]),
            "cos" => Math.Cos(evaluatedArgs[0]),
            "tan" => Math.Tan(evaluatedArgs[0]),
            "asin" => Math.Asin(evaluatedArgs[0]),
            "acos" => Math.Acos(evaluatedArgs[0]),
            "atan" => Math.Atan(evaluatedArgs[0]),
            "exp" => Math.Exp(evaluatedArgs[0]),
            "ln" or "log" => Math.Log(evaluatedArgs[0]),
            "sqrt" => Math.Sqrt(evaluatedArgs[0]),
            "abs" => Math.Abs(evaluatedArgs[0]),
            _ => throw new InvalidOperationException($"Function '{Name}' evaluation is not supported.")
        };
    }

    public override IEnumerable<string> GetVariables() =>
        Arguments.SelectMany(a => a.GetVariables()).Distinct();

    public override bool Equals(SymExpr? other) =>
        other is SymFunction fn && string.Equals(Name, fn.Name, StringComparison.Ordinal) &&
        Arguments.Count == fn.Arguments.Count && Arguments.SequenceEqual(fn.Arguments);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Name);
        foreach (var a in Arguments) hash.Add(a);
        return hash.ToHashCode();
    }
}
