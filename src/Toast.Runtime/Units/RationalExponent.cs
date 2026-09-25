namespace Tosh.Runtime.Units;

/// <summary>
/// Represents an exact rational exponent for physical dimensions (e.g. 1, 2, -1, 1/2).
/// Enables fractional dimensions in scientific computing such as noise spectral density
/// (V/sqrt(Hz) = V·s^(1/2)) and fracture mechanics (MPa·m^(1/2)).
/// </summary>
public readonly record struct RationalExponent : IEquatable<RationalExponent>, IComparable<RationalExponent>
{
    private readonly int _denominator;

    public int Numerator { get; }
    public int Denominator => _denominator == 0 ? 1 : _denominator;

    public static readonly RationalExponent Zero = new(0, 1);
    public static readonly RationalExponent One = new(1, 1);

    public RationalExponent(int numerator, int denominator = 1)
    {
        if (denominator == 0)
        {
            throw new DivideByZeroException("Denominator cannot be zero in a rational exponent.");
        }

        if (numerator == 0)
        {
            Numerator = 0;
            _denominator = 1;
            return;
        }

        long num = numerator;
        long den = denominator;

        if (den < 0)
        {
            num = -num;
            den = -den;
        }

        var gcd = GreatestCommonDivisor(Math.Abs(num), den);
        Numerator = checked((int)(num / gcd));
        _denominator = checked((int)(den / gcd));
    }

    private static long GreatestCommonDivisor(long a, long b)
    {
        while (b != 0)
        {
            var temp = b;
            b = a % b;
            a = temp;
        }
        return a;
    }

    public bool IsInteger => Denominator == 1;
    public bool IsZero => Numerator == 0;
    public double ToDouble() => (double)Numerator / Denominator;

    public static implicit operator RationalExponent(int value) => new(value, 1);

    public static explicit operator int(RationalExponent r) =>
        r.IsInteger ? r.Numerator : (int)Math.Round(r.ToDouble());

    public static RationalExponent operator +(RationalExponent a, RationalExponent b)
    {
        return new RationalExponent(
            checked(a.Numerator * b.Denominator + b.Numerator * a.Denominator),
            checked(a.Denominator * b.Denominator));
    }

    public static RationalExponent operator -(RationalExponent a, RationalExponent b)
    {
        return new RationalExponent(
            checked(a.Numerator * b.Denominator - b.Numerator * a.Denominator),
            checked(a.Denominator * b.Denominator));
    }

    public static RationalExponent operator *(RationalExponent a, RationalExponent b)
    {
        return new RationalExponent(
            checked(a.Numerator * b.Numerator),
            checked(a.Denominator * b.Denominator));
    }

    public static RationalExponent operator /(RationalExponent a, RationalExponent b)
    {
        if (b.Numerator == 0) throw new DivideByZeroException();
        return new RationalExponent(
            checked(a.Numerator * b.Denominator),
            checked(a.Denominator * b.Numerator));
    }

    public static RationalExponent operator -(RationalExponent a) => new(checked(-a.Numerator), a.Denominator);

    public int CompareTo(RationalExponent other)
    {
        long left = (long)Numerator * other.Denominator;
        long right = (long)other.Numerator * Denominator;
        return left.CompareTo(right);
    }

    public static bool operator <(RationalExponent a, RationalExponent b) => a.CompareTo(b) < 0;
    public static bool operator >(RationalExponent a, RationalExponent b) => a.CompareTo(b) > 0;
    public static bool operator <=(RationalExponent a, RationalExponent b) => a.CompareTo(b) <= 0;
    public static bool operator >=(RationalExponent a, RationalExponent b) => a.CompareTo(b) >= 0;

    public override string ToString() => IsInteger ? Numerator.ToString() : $"{Numerator}/{Denominator}";
}
