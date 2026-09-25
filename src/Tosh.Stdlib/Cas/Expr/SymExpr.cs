using System.Globalization;

namespace Tosh.Stdlib.Cas;

/// <summary>
/// Base class for all symbolic mathematical expressions in Tōast's Computer Algebra System.
/// Expressions form an immutable, composable tree supporting exact calculus and algebra.
/// </summary>
public abstract class SymExpr : IEquatable<SymExpr>
{
    public abstract int Precedence { get; }
    public virtual bool IsZero => false;
    public virtual bool IsOne => false;
    public virtual bool IsNumber => false;

    public abstract SymExpr Differentiate(string variable);
    public abstract SymExpr Substitute(string variable, SymExpr replacement);
    public abstract double Evaluate(IReadOnlyDictionary<string, double>? context = null);

    public static SymExpr Constant(string name, double value) => new SymConstant(name, value);
    public static SymExpr Variable(string name) => new SymVariable(name);
    public static SymExpr Number(BigRational value) => value.IsZero ? SymNumber.Zero : (value.IsOne ? SymNumber.One : new SymNumber(value));
    public static SymExpr Number(long value) => Number(new BigRational(value));
    public static SymExpr Number(double value)
    {
        // Try convert simple doubles to rational
        if (Math.Abs(value - Math.Round(value)) < 1e-10 && Math.Abs(value) < 1e12)
        {
            return Number((long)Math.Round(value));
        }
        return new SymNumber(new BigRational((long)(value * 1_000_000), 1_000_000));
    }

    public static readonly SymExpr Zero = SymNumber.Zero;
    public static readonly SymExpr One = SymNumber.One;
    public static readonly SymExpr Pi = new SymConstant("pi", Math.PI);
    public static readonly SymExpr E = new SymConstant("e", Math.E);

    public static SymExpr operator +(SymExpr a, SymExpr b) => Simplifier.Add(a, b);
    public static SymExpr operator -(SymExpr a, SymExpr b) => Simplifier.Subtract(a, b);
    public static SymExpr operator -(SymExpr a) => Simplifier.Negate(a);
    public static SymExpr operator *(SymExpr a, SymExpr b) => Simplifier.Multiply(a, b);
    public static SymExpr operator /(SymExpr a, SymExpr b) => Simplifier.Divide(a, b);
    public static SymExpr operator ^(SymExpr a, SymExpr b) => Simplifier.Power(a, b);

    public static implicit operator SymExpr(int value) => Number(value);
    public static implicit operator SymExpr(long value) => Number(value);
    public static implicit operator SymExpr(double value) => Number(value);
    public static implicit operator SymExpr(BigRational value) => Number(value);
    public static implicit operator SymExpr(string variable) => Variable(variable);

    public abstract bool Equals(SymExpr? other);
    public override bool Equals(object? obj) => obj is SymExpr other && Equals(other);
    public abstract override int GetHashCode();

    public override string ToString() => Format(this);

    public static string Format(SymExpr expr) => expr switch
    {
        SymNumber n => n.Value.ToString(),
        SymVariable v => v.Name,
        SymConstant c => c.Name,
        SymAdd add => FormatAdd(add),
        SymMul mul => FormatMul(mul),
        SymPow pow => $"{FormatSubExpr(pow.Base, pow.Precedence, isLeft: true)}^{FormatSubExpr(pow.Exponent, pow.Precedence, isLeft: false)}",
        SymFunction fn => $"{fn.Name}({string.Join(", ", fn.Arguments.Select(Format))})",
        _ => expr.ToString() ?? ""
    };

    private static string FormatAdd(SymAdd add)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < add.Terms.Count; i++)
        {
            var term = add.Terms[i];
            if (i == 0)
            {
                sb.Append(Format(term));
            }
            else
            {
                if (term is SymMul m && m.Factors.Count > 0 && m.Factors[0] is SymNumber num && num.Value.Sign < 0)
                {
                    sb.Append(" - ");
                    var positiveMul = Simplifier.Multiply(new SymNumber(-num.Value), m.Factors.Count == 2 ? m.Factors[1] : new SymMul(m.Factors.Skip(1).ToList()));
                    sb.Append(Format(positiveMul));
                }
                else if (term is SymNumber num2 && num2.Value.Sign < 0)
                {
                    sb.Append(" - ");
                    sb.Append(new SymNumber(-num2.Value));
                }
                else
                {
                    sb.Append(" + ");
                    sb.Append(Format(term));
                }
            }
        }
        return sb.ToString();
    }

    private static string FormatMul(SymMul mul)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < mul.Factors.Count; i++)
        {
            var factor = mul.Factors[i];
            var factorStr = FormatSubExpr(factor, mul.Precedence, isLeft: i == 0);
            if (i > 0)
            {
                sb.Append("*");
            }
            sb.Append(factorStr);
        }
        return sb.ToString();
    }

    private static string FormatSubExpr(SymExpr child, int parentPrecedence, bool isLeft)
    {
        var needParens = child.Precedence < parentPrecedence || (!isLeft && child.Precedence == parentPrecedence && child is SymPow);
        return needParens ? $"({Format(child)})" : Format(child);
    }
}
