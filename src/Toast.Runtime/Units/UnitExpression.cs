using System.Collections.ObjectModel;
using System.Text;

namespace Tosh.Runtime.Units;

/// <summary>
/// Represents a dimensional expression as a map of base dimensions to rational exponents.
/// For example, velocity (m/s) is { Length: 1, Time: -1 }, and noise spectral density
/// (V/sqrt(Hz)) is { Mass: 1, Length: 2, Time: -5/2, Current: -1 }.
/// Dimensionless quantities have an empty map.
/// </summary>
public sealed class UnitExpression : IEquatable<UnitExpression>
{
    public static readonly UnitExpression Dimensionless = new(new Dictionary<UnitDimension, RationalExponent>());

    private readonly ReadOnlyDictionary<UnitDimension, RationalExponent> _rationalExponents;
    private ReadOnlyDictionary<UnitDimension, int>? _intExponentsCache;

    public UnitExpression(Dictionary<UnitDimension, RationalExponent> exponents)
    {
        ArgumentNullException.ThrowIfNull(exponents);
        var normalized = new Dictionary<UnitDimension, RationalExponent>();

        foreach (var (dim, exp) in exponents)
        {
            if (!exp.IsZero)
            {
                normalized[dim] = exp;
            }
        }

        // Do not expose a mutable Dictionary: callers could mutate it after it became a key.
        _rationalExponents = new ReadOnlyDictionary<UnitDimension, RationalExponent>(normalized);
    }

    public UnitExpression(Dictionary<UnitDimension, int> exponents)
        : this(ToRationalDict(exponents))
    {
    }

    private static Dictionary<UnitDimension, RationalExponent> ToRationalDict(Dictionary<UnitDimension, int> exponents)
    {
        ArgumentNullException.ThrowIfNull(exponents);
        var dict = new Dictionary<UnitDimension, RationalExponent>();
        foreach (var (dim, exp) in exponents)
        {
            if (exp != 0)
            {
                dict[dim] = exp;
            }
        }
        return dict;
    }

    public static UnitExpression Of(UnitDimension dimension, RationalExponent exponent)
    {
        return new UnitExpression(new Dictionary<UnitDimension, RationalExponent> { [dimension] = exponent });
    }

    public static UnitExpression Of(UnitDimension dimension, int exponent = 1)
    {
        return Of(dimension, (RationalExponent)exponent);
    }

    public static UnitExpression Of(params (UnitDimension dim, RationalExponent exp)[] pairs)
    {
        var dict = new Dictionary<UnitDimension, RationalExponent>();
        foreach (var (dim, exp) in pairs)
        {
            if (!exp.IsZero) dict[dim] = exp;
        }
        return new UnitExpression(dict);
    }

    public static UnitExpression Of(params (UnitDimension dim, int exp)[] pairs)
    {
        var dict = new Dictionary<UnitDimension, RationalExponent>();
        foreach (var (dim, exp) in pairs)
        {
            if (exp != 0) dict[dim] = exp;
        }
        return new UnitExpression(dict);
    }

    public IReadOnlyDictionary<UnitDimension, RationalExponent> RationalExponents => _rationalExponents;

    public IReadOnlyDictionary<UnitDimension, int> Exponents
    {
        get
        {
            return _intExponentsCache ??= new ReadOnlyDictionary<UnitDimension, int>(
                _rationalExponents.ToDictionary(kvp => kvp.Key, kvp => (int)kvp.Value));
        }
    }

    public bool IsDimensionless => _rationalExponents.Count == 0;

    public int GetExponent(UnitDimension dimension)
    {
        return _rationalExponents.TryGetValue(dimension, out var exp) ? (int)exp : 0;
    }

    public RationalExponent GetRationalExponent(UnitDimension dimension)
    {
        return _rationalExponents.TryGetValue(dimension, out var exp) ? exp : RationalExponent.Zero;
    }

    /// <summary>
    /// Multiplies two dimension expressions by adding exponents.
    /// </summary>
    public UnitExpression Multiply(UnitExpression other)
    {
        var result = new Dictionary<UnitDimension, RationalExponent>(_rationalExponents);

        foreach (var (dim, exp) in other._rationalExponents)
        {
            result.TryGetValue(dim, out var current);
            var sum = current + exp;
            if (sum.IsZero)
            {
                result.Remove(dim);
            }
            else
            {
                result[dim] = sum;
            }
        }

        return new UnitExpression(result);
    }

    /// <summary>
    /// Divides by another dimension expression (subtracts exponents).
    /// </summary>
    public UnitExpression Divide(UnitExpression other)
    {
        var result = new Dictionary<UnitDimension, RationalExponent>(_rationalExponents);

        foreach (var (dim, exp) in other._rationalExponents)
        {
            result.TryGetValue(dim, out var current);
            var diff = current - exp;
            if (diff.IsZero)
            {
                result.Remove(dim);
            }
            else
            {
                result[dim] = diff;
            }
        }

        return new UnitExpression(result);
    }

    /// <summary>
    /// Raises every exponent to a power (e.g. squaring m → m²).
    /// </summary>
    public UnitExpression Power(int power) => Power((RationalExponent)power);

    /// <summary>
    /// Raises every exponent to a rational power (e.g. m^(1/2)).
    /// </summary>
    public UnitExpression Power(RationalExponent power)
    {
        if (power.IsZero) return Dimensionless;

        var result = new Dictionary<UnitDimension, RationalExponent>();
        foreach (var (dim, exp) in _rationalExponents)
        {
            var product = exp * power;
            if (!product.IsZero)
            {
                result[dim] = product;
            }
        }

        return new UnitExpression(result);
    }

    /// <summary>
    /// Takes a root of the dimension (e.g. root 2 of m^2 is m; root 2 of Hz is s^(-1/2)).
    /// </summary>
    public UnitExpression Root(int root)
    {
        if (root == 0) throw new DivideByZeroException("Root degree cannot be zero.");
        return Power(new RationalExponent(1, root));
    }

    /// <summary>
    /// Returns the reciprocal (all exponents negated).
    /// </summary>
    public UnitExpression Reciprocal() => Power(-1);

    public bool Equals(UnitExpression? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        if (_rationalExponents.Count != other._rationalExponents.Count) return false;

        foreach (var (dim, exp) in _rationalExponents)
        {
            if (!other._rationalExponents.TryGetValue(dim, out var otherExp) || exp != otherExp)
            {
                return false;
            }
        }

        return true;
    }

    public override bool Equals(object? obj) => Equals(obj as UnitExpression);

    public override int GetHashCode()
    {
        var hash = new HashCode();

        foreach (var (dim, exp) in _rationalExponents.OrderBy(static e => e.Key))
        {
            hash.Add(dim);
            hash.Add(exp);
        }

        return hash.ToHashCode();
    }

    public static bool operator ==(UnitExpression? left, UnitExpression? right) =>
        left is null ? right is null : left.Equals(right);

    public static bool operator !=(UnitExpression? left, UnitExpression? right) =>
        !(left == right);

    private static readonly Dictionary<UnitDimension, string> DimensionSymbols = new()
    {
        [UnitDimension.Length] = "m",
        [UnitDimension.Mass] = "kg",
        [UnitDimension.Time] = "s",
        [UnitDimension.ElectricCurrent] = "A",
        [UnitDimension.Temperature] = "K",
        [UnitDimension.AmountOfSubstance] = "mol",
        [UnitDimension.LuminousIntensity] = "cd",
        // The unit registry's data base is the bit. Bytes are a display unit with
        // factor 8; spelling the canonical base as B silently scaled derived data.
        [UnitDimension.Data] = "bit",
        [UnitDimension.Angle] = "rad",
    };

    /// <summary>
    /// Formats as SI base dimensions, e.g. "kg·m/s²" or "V·s^(1/2)".
    /// </summary>
    public string ToSymbolString()
    {
        return FormatSymbol(useSuperscripts: true);
    }

    /// <summary>
    /// Formats a canonical unit expression that <see cref="UnitExpressionParser"/>
    /// can read again, e.g. <c>kg·m^2/s^3</c>. Every component is a base unit, so
    /// the resulting conversion factor is one.
    /// </summary>
    public string ToCanonicalUnitSymbol()
    {
        return FormatSymbol(useSuperscripts: false);
    }

    private string FormatSymbol(bool useSuperscripts)
    {
        if (IsDimensionless) return "";

        var numerator = new List<(string sym, RationalExponent exp)>();
        var denominator = new List<(string sym, RationalExponent exp)>();

        foreach (var (dim, exp) in _rationalExponents.OrderBy(static e => e.Key))
        {
            var sym = DimensionSymbols.TryGetValue(dim, out var s) ? s : dim.ToString();

            if (exp.Numerator > 0)
            {
                numerator.Add((sym, exp));
            }
            else if (exp.Numerator < 0)
            {
                denominator.Add((sym, -exp));
            }
        }

        var sb = new StringBuilder();

        for (var i = 0; i < numerator.Count; i++)
        {
            if (i > 0) sb.Append('·');
            sb.Append(numerator[i].sym);

            if (numerator[i].exp != RationalExponent.One)
            {
                AppendExponent(sb, numerator[i].exp, useSuperscripts);
            }
        }

        if (denominator.Count > 0)
        {
            if (numerator.Count == 0) sb.Append('1');

            if (!useSuperscripts)
            {
                // The source grammar deliberately has no unit grouping. Repeated
                // division is left-associative and round-trips every denominator
                // vector: m/kg/s^2, never the unparseable m/(kg·s^2) or the
                // dimensionally different m/kg·s^2.
                foreach (var (sym, exp) in denominator)
                {
                    sb.Append('/').Append(sym);
                    if (exp != RationalExponent.One) AppendExponent(sb, exp, useSuperscripts: false);
                }

                return sb.ToString();
            }

            sb.Append('/');
            var needParens = denominator.Count > 1;
            if (needParens) sb.Append('(');
            for (var i = 0; i < denominator.Count; i++)
            {
                if (i > 0) sb.Append('·');
                sb.Append(denominator[i].sym);
                if (denominator[i].exp != RationalExponent.One) AppendExponent(sb, denominator[i].exp, useSuperscripts: true);
            }
            if (needParens) sb.Append(')');
        }

        return sb.ToString();
    }

    public override string ToString() => ToSymbolString();

    private static void AppendExponent(StringBuilder builder, RationalExponent exponent, bool useSuperscripts)
    {
        if (exponent.IsInteger)
        {
            if (useSuperscripts)
            {
                builder.Append(FormatSuperscript(exponent.Numerator));
            }
            else
            {
                builder.Append('^').Append(exponent.Numerator);
            }
        }
        else
        {
            builder.Append($"^({exponent.Numerator}/{exponent.Denominator})");
        }
    }

    private static string FormatSuperscript(int value)
    {
        return value.ToString().Replace("0", "⁰").Replace("1", "¹").Replace("2", "²")
            .Replace("3", "³").Replace("4", "⁴").Replace("5", "⁵").Replace("6", "⁶")
            .Replace("7", "⁷").Replace("8", "⁸").Replace("9", "⁹");
    }
}
