using System.Globalization;
using System.Numerics;

namespace Tosh.Stdlib.Cas;

/// <summary>
/// Exact rational number represented as Numerator / Denominator using BigInteger.
/// Reduced to lowest terms and normalized so Denominator is always positive.
/// </summary>
public readonly struct BigRational : IComparable<BigRational>, IEquatable<BigRational>, IFormattable
{
    public BigInteger Numerator { get; }
    public BigInteger Denominator { get; }

    public static readonly BigRational Zero = new(0, 1);
    public static readonly BigRational One = new(1, 1);
    public static readonly BigRational MinusOne = new(-1, 1);
    public static readonly BigRational Half = new(1, 2);

    public BigRational(BigInteger numerator, BigInteger denominator)
    {
        if (denominator.IsZero)
        {
            throw new DivideByZeroException("Denominator cannot be zero.");
        }

        if (denominator < 0)
        {
            numerator = -numerator;
            denominator = -denominator;
        }

        if (numerator.IsZero)
        {
            Numerator = BigInteger.Zero;
            Denominator = BigInteger.One;
        }
        else
        {
            var gcd = BigInteger.GreatestCommonDivisor(BigInteger.Abs(numerator), denominator);
            Numerator = numerator / gcd;
            Denominator = denominator / gcd;
        }
    }

    public BigRational(long value) : this(new BigInteger(value), BigInteger.One) { }
    public BigRational(BigInteger value) : this(value, BigInteger.One) { }

    public bool IsZero => Numerator.IsZero;
    public bool IsOne => Numerator == Denominator;
    public bool IsInteger => Denominator.IsOne;
    public int Sign => Numerator.Sign;

    public static BigRational operator +(BigRational a, BigRational b) =>
        new(a.Numerator * b.Denominator + b.Numerator * a.Denominator, a.Denominator * b.Denominator);

    public static BigRational operator -(BigRational a, BigRational b) =>
        new(a.Numerator * b.Denominator - b.Numerator * a.Denominator, a.Denominator * b.Denominator);

    public static BigRational operator -(BigRational a) =>
        new(-a.Numerator, a.Denominator);

    public static BigRational operator *(BigRational a, BigRational b) =>
        new(a.Numerator * b.Numerator, a.Denominator * b.Denominator);

    public static BigRational operator /(BigRational a, BigRational b)
    {
        if (b.IsZero) throw new DivideByZeroException();
        return new(a.Numerator * b.Denominator, a.Denominator * b.Numerator);
    }

    public static BigRational operator +(BigRational a, long b) => a + new BigRational(b);
    public static BigRational operator *(BigRational a, long b) => a * new BigRational(b);

    public BigRational Abs() => Numerator < 0 ? -this : this;

    public BigRational Pow(int exponent)
    {
        if (exponent == 0) return One;
        if (exponent > 0) return new(BigInteger.Pow(Numerator, exponent), BigInteger.Pow(Denominator, exponent));
        if (IsZero) throw new DivideByZeroException();
        return new(BigInteger.Pow(Denominator, -exponent), BigInteger.Pow(Numerator, -exponent));
    }

    public double ToDouble() => (double)Numerator / (double)Denominator;

    public static BigRational FromDouble(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            throw new ArgumentException("Cannot convert NaN or Infinity to BigRational.", nameof(value));
        }

        if (Math.Abs(value - Math.Round(value)) < 1e-10 && Math.Abs(value) < 1e15)
        {
            return new BigRational((long)Math.Round(value));
        }

        try
        {
            decimal dec = (decimal)value;
            int[] bits = decimal.GetBits(dec);
            byte scale = (byte)((bits[3] >> 16) & 0x7F);
            bool sign = (bits[3] & 0x80000000) != 0;
            BigInteger unscaled = (new BigInteger((uint)bits[2]) << 64) |
                                  (new BigInteger((uint)bits[1]) << 32) |
                                  new BigInteger((uint)bits[0]);
            if (sign) unscaled = -unscaled;
            BigInteger denom = BigInteger.Pow(10, scale);
            return new BigRational(unscaled, denom);
        }
        catch (OverflowException)
        {
            return new BigRational((long)(value * 1_000_000), 1_000_000);
        }
    }

    public static implicit operator BigRational(long value) => new(value);
    public static implicit operator BigRational(int value) => new(value);
    public static explicit operator double(BigRational value) => value.ToDouble();

    public bool Equals(BigRational other) => Numerator == other.Numerator && Denominator == other.Denominator;
    public override bool Equals(object? obj) => obj is BigRational other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Numerator, Denominator);

    public int CompareTo(BigRational other)
    {
        var diff = Numerator * other.Denominator - other.Numerator * Denominator;
        return diff.Sign;
    }

    public static bool operator ==(BigRational a, BigRational b) => a.Equals(b);
    public static bool operator !=(BigRational a, BigRational b) => !a.Equals(b);
    public static bool operator <(BigRational a, BigRational b) => a.CompareTo(b) < 0;
    public static bool operator <=(BigRational a, BigRational b) => a.CompareTo(b) <= 0;
    public static bool operator >(BigRational a, BigRational b) => a.CompareTo(b) > 0;
    public static bool operator >=(BigRational a, BigRational b) => a.CompareTo(b) >= 0;

    public override string ToString() =>
        IsInteger ? Numerator.ToString(CultureInfo.InvariantCulture) : $"{Numerator}/{Denominator}";

    public string ToString(string? format, IFormatProvider? formatProvider) =>
        IsInteger ? Numerator.ToString(format, formatProvider) : $"{Numerator.ToString(format, formatProvider)}/{Denominator.ToString(format, formatProvider)}";
}
