using System.Numerics;

namespace Tosh.Core;

/// <summary>
/// Exposes mathematical functions and constants as a shell static type.
/// Usage: Math.sqrt(16), Math.PI, Math.sin(1.0), Math.factorial(20), etc.
/// </summary>
public sealed class MathShellType : IShellStaticType
{
    public static readonly MathShellType Instance = new();

    public string ShellTypeName => "Math";

    public object CreateInstance(IReadOnlyList<object?> arguments)
    {
        throw new InvalidOperationException("Math is a static type and cannot be instantiated. Use Math.method() or Math.CONSTANT.");
    }

    public InvocationResult InvokeStaticMethod(string methodName, IReadOnlyList<object?> arguments)
    {
        var result = methodName.ToLowerInvariant() switch
        {
            // Trigonometric
            "sin" => Math.Sin(ToDouble(arguments, 0)),
            "cos" => Math.Cos(ToDouble(arguments, 0)),
            "tan" => Math.Tan(ToDouble(arguments, 0)),
            "asin" => Math.Asin(ToDouble(arguments, 0)),
            "acos" => Math.Acos(ToDouble(arguments, 0)),
            "atan" => Math.Atan(ToDouble(arguments, 0)),
            "atan2" => Math.Atan2(ToDouble(arguments, 0), ToDouble(arguments, 1)),
            "sinh" => Math.Sinh(ToDouble(arguments, 0)),
            "cosh" => Math.Cosh(ToDouble(arguments, 0)),
            "tanh" => Math.Tanh(ToDouble(arguments, 0)),

            // Exponential / logarithmic
            "exp" => Math.Exp(ToDouble(arguments, 0)),
            "log" => arguments.Count >= 2
                ? Math.Log(ToDouble(arguments, 0), ToDouble(arguments, 1))
                : Math.Log(ToDouble(arguments, 0)),
            "log2" => Math.Log2(ToDouble(arguments, 0)),
            "log10" => Math.Log10(ToDouble(arguments, 0)),
            "pow" => Math.Pow(ToDouble(arguments, 0), ToDouble(arguments, 1)),
            "sqrt" => Math.Sqrt(ToDouble(arguments, 0)),
            "cbrt" => Math.Cbrt(ToDouble(arguments, 0)),

            // Rounding
            "abs" => MathAbs(arguments),
            "ceil" or "ceiling" => Math.Ceiling(ToDouble(arguments, 0)),
            "floor" => Math.Floor(ToDouble(arguments, 0)),
            "round" => arguments.Count >= 2
                ? Math.Round(ToDouble(arguments, 0), ToInt(arguments, 1))
                : Math.Round(ToDouble(arguments, 0)),
            "truncate" => Math.Truncate(ToDouble(arguments, 0)),
            "sign" => Math.Sign(ToDouble(arguments, 0)),
            "clamp" => Math.Clamp(ToDouble(arguments, 0), ToDouble(arguments, 1), ToDouble(arguments, 2)),

            // Min/Max
            "min" => Math.Min(ToDouble(arguments, 0), ToDouble(arguments, 1)),
            "max" => Math.Max(ToDouble(arguments, 0), ToDouble(arguments, 1)),

            // Integer math
            "gcd" => BigInteger.GreatestCommonDivisor(ToBigInteger(arguments, 0), ToBigInteger(arguments, 1)),
            "lcm" => Lcm(ToBigInteger(arguments, 0), ToBigInteger(arguments, 1)),
            "factorial" => Factorial(ToBigInteger(arguments, 0)),

            // Conversion
            "to-radians" or "toradians" => ToDouble(arguments, 0) * Math.PI / 180.0,
            "to-degrees" or "todegrees" => ToDouble(arguments, 0) * 180.0 / Math.PI,

            // Combinatorics
            "choose" or "binomial" => BinomialCoefficient(ToBigInteger(arguments, 0), ToBigInteger(arguments, 1)),
            "permutations" => Permutations(ToBigInteger(arguments, 0), ToBigInteger(arguments, 1)),

            // Misc
            "hypot" => Math.Sqrt(
                Math.Pow(ToDouble(arguments, 0), 2) +
                Math.Pow(ToDouble(arguments, 1), 2)),
            "is-prime" or "isprime" => IsPrime(ToBigInteger(arguments, 0)),

            _ => throw new InvalidOperationException(
                $"Math.{methodName} is not a recognized function. " +
                $"Available: sin, cos, tan, asin, acos, atan, atan2, sinh, cosh, tanh, " +
                $"exp, log, log2, log10, pow, sqrt, cbrt, abs, ceil, floor, round, truncate, " +
                $"sign, clamp, min, max, gcd, lcm, factorial, to-radians, to-degrees, " +
                $"choose, permutations, hypot, is-prime."),
        };

        return new InvocationResult(result, false);
    }

    public bool TryGetStaticMember(string memberName, out object? value)
    {
        value = memberName.ToLowerInvariant() switch
        {
            "pi" => Math.PI,
            "e" => Math.E,
            "tau" => Math.Tau,
            "infinity" or "inf" => double.PositiveInfinity,
            "negative-infinity" or "neginf" => double.NegativeInfinity,
            "nan" => double.NaN,
            "epsilon" => double.Epsilon,
            "max-value" or "maxvalue" => double.MaxValue,
            "min-value" or "minvalue" => double.MinValue,
            _ => null,
        };

        return value is not null;
    }

    // --- Helpers ---

    private static double ToDouble(IReadOnlyList<object?> arguments, int index)
    {
        if (index >= arguments.Count)
            throw new InvalidOperationException($"Math function expected at least {index + 1} argument(s), got {arguments.Count}.");

        return arguments[index] switch
        {
            double d => d,
            float f => f,
            decimal m => (double)m,
            int i => i,
            long l => l,
            BigInteger bi => (double)bi,
            string s when double.TryParse(s, out var parsed) => parsed,
            null => throw new InvalidOperationException("Math function received a null argument."),
            _ => Convert.ToDouble(arguments[index]),
        };
    }

    private static int ToInt(IReadOnlyList<object?> arguments, int index)
    {
        if (index >= arguments.Count)
            throw new InvalidOperationException($"Math function expected at least {index + 1} argument(s), got {arguments.Count}.");

        return arguments[index] switch
        {
            int i => i,
            long l => (int)l,
            double d => (int)d,
            BigInteger bi => (int)bi,
            string s when int.TryParse(s, out var parsed) => parsed,
            null => throw new InvalidOperationException("Math function received a null argument."),
            _ => Convert.ToInt32(arguments[index]),
        };
    }

    private static BigInteger ToBigInteger(IReadOnlyList<object?> arguments, int index)
    {
        if (index >= arguments.Count)
            throw new InvalidOperationException($"Math function expected at least {index + 1} argument(s), got {arguments.Count}.");

        return arguments[index] switch
        {
            BigInteger bi => bi,
            int i => i,
            long l => l,
            double d => new BigInteger(d),
            string s when BigInteger.TryParse(s, out var parsed) => parsed,
            null => throw new InvalidOperationException("Math function received a null argument."),
            _ => new BigInteger(Convert.ToDouble(arguments[index])),
        };
    }

    private static object MathAbs(IReadOnlyList<object?> arguments)
    {
        var arg = arguments[0];
        return arg switch
        {
            int i => Math.Abs(i),
            long l => Math.Abs(l),
            double d => Math.Abs(d),
            decimal m => Math.Abs(m),
            float f => Math.Abs(f),
            BigInteger bi => BigInteger.Abs(bi),
            _ => Math.Abs(ToDouble(arguments, 0)),
        };
    }

    private static BigInteger Factorial(BigInteger n)
    {
        if (n < 0) throw new InvalidOperationException("Math.factorial requires a non-negative integer.");
        if (n <= 1) return BigInteger.One;

        BigInteger result = BigInteger.One;
        for (BigInteger i = 2; i <= n; i++)
            result *= i;

        return result;
    }

    private static BigInteger Lcm(BigInteger a, BigInteger b)
    {
        if (a == 0 && b == 0) return BigInteger.Zero;
        return BigInteger.Abs(a / BigInteger.GreatestCommonDivisor(a, b) * b);
    }

    private static BigInteger BinomialCoefficient(BigInteger n, BigInteger k)
    {
        if (k < 0 || k > n) return BigInteger.Zero;
        if (k == 0 || k == n) return BigInteger.One;
        if (k > n - k) k = n - k;

        BigInteger result = BigInteger.One;
        for (BigInteger i = 0; i < k; i++)
        {
            result = result * (n - i) / (i + 1);
        }

        return result;
    }

    private static BigInteger Permutations(BigInteger n, BigInteger k)
    {
        if (k < 0 || k > n) return BigInteger.Zero;

        BigInteger result = BigInteger.One;
        for (BigInteger i = 0; i < k; i++)
        {
            result *= (n - i);
        }

        return result;
    }

    private static bool IsPrime(BigInteger n)
    {
        if (n < 2) return false;
        if (n < 4) return true;
        if (n % 2 == 0 || n % 3 == 0) return false;

        for (BigInteger i = 5; i * i <= n; i += 6)
        {
            if (n % i == 0 || n % (i + 2) == 0)
                return false;
        }

        return true;
    }
}
