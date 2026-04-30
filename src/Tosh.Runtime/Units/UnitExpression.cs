using System.Text;

namespace Tosh.Runtime.Units;

/// <summary>
/// Represents a dimensional expression as a map of base dimensions to integer exponents.
/// For example, velocity (m/s) is { Length: 1, Time: -1 }.
/// Dimensionless quantities have an empty map.
/// </summary>
public sealed class UnitExpression : IEquatable<UnitExpression>
{
    public static readonly UnitExpression Dimensionless = new(new Dictionary<UnitDimension, int>());

    private readonly Dictionary<UnitDimension, int> _exponents;

    public UnitExpression(Dictionary<UnitDimension, int> exponents)
    {
        _exponents = new Dictionary<UnitDimension, int>();

        foreach (var (dim, exp) in exponents)
        {
            if (exp != 0)
            {
                _exponents[dim] = exp;
            }
        }
    }

    public static UnitExpression Of(UnitDimension dimension, int exponent = 1)
    {
        return new UnitExpression(new Dictionary<UnitDimension, int> { [dimension] = exponent });
    }

    public static UnitExpression Of(params (UnitDimension dim, int exp)[] pairs)
    {
        var dict = new Dictionary<UnitDimension, int>();

        foreach (var (dim, exp) in pairs)
        {
            dict[dim] = exp;
        }

        return new UnitExpression(dict);
    }

    public IReadOnlyDictionary<UnitDimension, int> Exponents => _exponents;

    public bool IsDimensionless => _exponents.Count == 0;

    public int GetExponent(UnitDimension dimension)
    {
        return _exponents.TryGetValue(dimension, out var exp) ? exp : 0;
    }

    /// <summary>
    /// Multiplies two dimension expressions by adding exponents.
    /// </summary>
    public UnitExpression Multiply(UnitExpression other)
    {
        var result = new Dictionary<UnitDimension, int>(_exponents);

        foreach (var (dim, exp) in other._exponents)
        {
            result.TryGetValue(dim, out var current);
            result[dim] = current + exp;
        }

        return new UnitExpression(result);
    }

    /// <summary>
    /// Divides by another dimension expression (subtracts exponents).
    /// </summary>
    public UnitExpression Divide(UnitExpression other)
    {
        var result = new Dictionary<UnitDimension, int>(_exponents);

        foreach (var (dim, exp) in other._exponents)
        {
            result.TryGetValue(dim, out var current);
            result[dim] = current - exp;
        }

        return new UnitExpression(result);
    }

    /// <summary>
    /// Raises every exponent to a power (e.g. squaring m → m²).
    /// </summary>
    public UnitExpression Power(int power)
    {
        var result = new Dictionary<UnitDimension, int>();

        foreach (var (dim, exp) in _exponents)
        {
            result[dim] = exp * power;
        }

        return new UnitExpression(result);
    }

    /// <summary>
    /// Returns the reciprocal (all exponents negated).
    /// </summary>
    public UnitExpression Reciprocal()
    {
        var result = new Dictionary<UnitDimension, int>();

        foreach (var (dim, exp) in _exponents)
        {
            result[dim] = -exp;
        }

        return new UnitExpression(result);
    }

    public bool Equals(UnitExpression? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        if (_exponents.Count != other._exponents.Count) return false;

        foreach (var (dim, exp) in _exponents)
        {
            if (!other._exponents.TryGetValue(dim, out var otherExp) || exp != otherExp)
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

        foreach (var (dim, exp) in _exponents.OrderBy(static e => e.Key))
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
        [UnitDimension.Data] = "B",
        [UnitDimension.Angle] = "rad",
    };

    /// <summary>
    /// Formats as SI base dimensions, e.g. "kg·m/s²".
    /// </summary>
    public string ToSymbolString()
    {
        if (IsDimensionless) return "";

        var numerator = new List<(string sym, int exp)>();
        var denominator = new List<(string sym, int exp)>();

        foreach (var (dim, exp) in _exponents.OrderBy(static e => e.Key))
        {
            var sym = DimensionSymbols.TryGetValue(dim, out var s) ? s : dim.ToString();

            if (exp > 0)
            {
                numerator.Add((sym, exp));
            }
            else if (exp < 0)
            {
                denominator.Add((sym, -exp));
            }
        }

        var sb = new StringBuilder();

        for (var i = 0; i < numerator.Count; i++)
        {
            if (i > 0) sb.Append('·');
            sb.Append(numerator[i].sym);

            if (numerator[i].exp > 1)
            {
                sb.Append(FormatSuperscript(numerator[i].exp));
            }
        }

        if (denominator.Count > 0)
        {
            if (numerator.Count == 0) sb.Append('1');
            sb.Append('/');

            var needParens = denominator.Count > 1;
            if (needParens) sb.Append('(');

            for (var i = 0; i < denominator.Count; i++)
            {
                if (i > 0) sb.Append('·');
                sb.Append(denominator[i].sym);

                if (denominator[i].exp > 1)
                {
                    sb.Append(FormatSuperscript(denominator[i].exp));
                }
            }

            if (needParens) sb.Append(')');
        }

        return sb.ToString();
    }

    public override string ToString() => ToSymbolString();

    private static string FormatSuperscript(int value)
    {
        return value.ToString().Replace("0", "⁰").Replace("1", "¹").Replace("2", "²")
            .Replace("3", "³").Replace("4", "⁴").Replace("5", "⁵").Replace("6", "⁶")
            .Replace("7", "⁷").Replace("8", "⁸").Replace("9", "⁹");
    }
}
