using System.Globalization;
using System.Numerics;
using Tosh.Runtime;

namespace Tosh.Stdlib.Cas;

/// <summary>
/// Base class for all symbolic mathematical expressions in Tōast's Computer Algebra System.
/// Expressions form an immutable, composable tree supporting exact calculus and algebra.
/// </summary>
public abstract class SymExpr : IEquatable<SymExpr>, IShellReversibleBinaryOperatorObject, IShellMathFunctionObject
{
    public abstract int Precedence { get; }
    public virtual bool IsZero => false;
    public virtual bool IsOne => false;
    public virtual bool IsNumber => false;

    public abstract SymExpr Differentiate(string variable);
    public abstract SymExpr Substitute(string variable, SymExpr replacement);
    public abstract double Evaluate(IReadOnlyDictionary<string, double>? context = null);
    public virtual double Approximate => Evaluate();
    public virtual IEnumerable<string> GetVariables() => Enumerable.Empty<string>();

    public virtual bool TryEvaluateMathFunction(
        string functionName,
        IReadOnlyList<object?> additionalArguments,
        out object? result)
    {
        var fn = functionName.ToLowerInvariant();
        switch (fn)
        {
            case "sin" or "cos" or "tan" or "asin" or "acos" or "atan" or "sinh" or "cosh" or "tanh" or "sqrt" or "cbrt" or "exp" or "abs":
                result = Simplifier.Simplify(new SymFunction(fn, [this]));
                return true;
            case "log" or "ln":
                result = Simplifier.Simplify(new SymFunction("ln", [this]));
                return true;
            case "pow":
                if (additionalArguments.Count > 0 && TryCoerce(additionalArguments[0], out var exp))
                {
                    result = Simplifier.Power(this, exp);
                    return true;
                }
                break;
        }
        result = null;
        return false;
    }

    public static SymExpr Constant(string name, double value) => new SymConstant(name, value);
    public static SymExpr Variable(string name) => new SymVariable(name);
    public static SymExpr Number(BigRational value) => value.IsZero ? SymNumber.Zero : (value.IsOne ? SymNumber.One : new SymNumber(value));
    public static SymExpr Number(long value) => Number(new BigRational(value));
    public static SymExpr Number(BigInteger value) => Number(new BigRational(value));
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
    public static implicit operator SymExpr(BigInteger value) => Number(value);
    public static implicit operator SymExpr(string variable) => Variable(variable);

    public virtual bool TryEvaluateBinaryOperator(
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

        if (operatorName is not ("==" or "="))
        {
            if (this is SymEquation eqThis)
            {
                return eqThis.TryEvaluateBinaryOperator(operatorName, otherExpr, reversed, out value);
            }

            if (otherExpr is SymEquation eqOther)
            {
                return eqOther.TryEvaluateBinaryOperator(operatorName, this, !reversed, out value);
            }
        }

        var lhs = reversed ? otherExpr : this;
        var rhs = reversed ? this : otherExpr;

        switch (operatorName)
        {
            case "+":
                value = Simplifier.Add(lhs, rhs);
                return true;
            case "-":
                value = Simplifier.Subtract(lhs, rhs);
                return true;
            case "*":
                value = Simplifier.Multiply(lhs, rhs);
                return true;
            case "/":
                value = Simplifier.Divide(lhs, rhs);
                return true;
            case "**" or "^":
                value = Simplifier.Power(lhs, rhs);
                return true;
            case "==" or "=":
                value = new SymEquation(lhs, rhs);
                return true;
            default:
                return false;
        }
    }

    protected internal static bool TryCoerce(object? obj, out SymExpr expr)
    {
        if (obj is SymExpr se) { expr = se; return true; }
        if (obj is int i) { expr = Number(i); return true; }
        if (obj is long l) { expr = Number(l); return true; }
        if (obj is double d) { expr = Number(d); return true; }
        if (obj is float f) { expr = Number(f); return true; }
        if (obj is decimal m) { expr = Number((double)m); return true; }
        if (obj is BigInteger bi) { expr = Number(bi); return true; }
        if (obj is BigRational br) { expr = Number(br); return true; }
        if (obj is string s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var pd))
        {
            expr = Number(pd);
            return true;
        }
        expr = Zero;
        return false;
    }

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
        SymEquation eq => $"{Format(eq.Left)} = {Format(eq.Right)}",
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
