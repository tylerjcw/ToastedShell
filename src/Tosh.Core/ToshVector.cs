using System.Collections;
using System.Globalization;
using System.Text;

namespace Tosh.Core;

/// <summary>
/// A fixed-size numeric vector for linear algebra and scientific computing.
/// Wraps a <c>double[]</c> and provides element-wise arithmetic, dot/cross products,
/// magnitude, normalization, and pipeline-friendly enumeration.
/// </summary>
public sealed class ToshVector : IShellRecordObject, IFormattable, IEnumerable<double>, IComparable
{
    private readonly double[] _elements;

    public ToshVector(double[] elements)
    {
        _elements = elements ?? throw new ArgumentNullException(nameof(elements));
    }

    public ToshVector(IEnumerable<double> elements)
    {
        _elements = elements.ToArray();
    }

    public int Length => _elements.Length;

    public double this[int index]
    {
        get
        {
            if (index < 0 || index >= _elements.Length)
                throw new IndexOutOfRangeException($"Index {index} is out of range for vector of length {_elements.Length}.");
            return _elements[index];
        }
    }

    /// <summary>Returns a slice of this vector from <paramref name="start"/> (inclusive) to <paramref name="end"/> (exclusive).</summary>
    public ToshVector Slice(int start, int end)
    {
        if (start < 0) start = 0;
        if (end > _elements.Length) end = _elements.Length;
        if (start >= end) return new ToshVector([]);
        var slice = new double[end - start];
        Array.Copy(_elements, start, slice, 0, slice.Length);
        return new ToshVector(slice);
    }

    /// <summary>Returns the underlying elements as a read-only span.</summary>
    public ReadOnlySpan<double> AsSpan() => _elements.AsSpan();

    /// <summary>Returns a copy of the underlying array.</summary>
    public double[] ToArray() => (double[])_elements.Clone();

    #region Element-wise Arithmetic

    public static ToshVector operator +(ToshVector left, ToshVector right)
    {
        EnsureSameLength(left, right, "+");
        var result = new double[left.Length];
        for (int i = 0; i < result.Length; i++)
            result[i] = left._elements[i] + right._elements[i];
        return new ToshVector(result);
    }

    public static ToshVector operator -(ToshVector left, ToshVector right)
    {
        EnsureSameLength(left, right, "-");
        var result = new double[left.Length];
        for (int i = 0; i < result.Length; i++)
            result[i] = left._elements[i] - right._elements[i];
        return new ToshVector(result);
    }

    /// <summary>Element-wise (Hadamard) product.</summary>
    public static ToshVector operator *(ToshVector left, ToshVector right)
    {
        EnsureSameLength(left, right, "*");
        var result = new double[left.Length];
        for (int i = 0; i < result.Length; i++)
            result[i] = left._elements[i] * right._elements[i];
        return new ToshVector(result);
    }

    /// <summary>Element-wise division.</summary>
    public static ToshVector operator /(ToshVector left, ToshVector right)
    {
        EnsureSameLength(left, right, "/");
        var result = new double[left.Length];
        for (int i = 0; i < result.Length; i++)
        {
            if (right._elements[i] == 0)
                throw new DivideByZeroException($"Division by zero at vector element {i}.");
            result[i] = left._elements[i] / right._elements[i];
        }
        return new ToshVector(result);
    }

    /// <summary>Scalar multiplication (vector * scalar).</summary>
    public static ToshVector operator *(ToshVector vector, double scalar)
    {
        var result = new double[vector.Length];
        for (int i = 0; i < result.Length; i++)
            result[i] = vector._elements[i] * scalar;
        return new ToshVector(result);
    }

    /// <summary>Scalar multiplication (scalar * vector).</summary>
    public static ToshVector operator *(double scalar, ToshVector vector) => vector * scalar;

    /// <summary>Scalar division.</summary>
    public static ToshVector operator /(ToshVector vector, double scalar)
    {
        if (scalar == 0)
            throw new DivideByZeroException("Cannot divide vector by zero.");
        var result = new double[vector.Length];
        for (int i = 0; i < result.Length; i++)
            result[i] = vector._elements[i] / scalar;
        return new ToshVector(result);
    }

    /// <summary>Unary negation.</summary>
    public static ToshVector operator -(ToshVector vector)
    {
        var result = new double[vector.Length];
        for (int i = 0; i < result.Length; i++)
            result[i] = -vector._elements[i];
        return new ToshVector(result);
    }

    #endregion

    #region Linear Algebra

    /// <summary>Dot product of two vectors.</summary>
    public static double Dot(ToshVector a, ToshVector b)
    {
        EnsureSameLength(a, b, "dot");
        double sum = 0;
        for (int i = 0; i < a.Length; i++)
            sum += a._elements[i] * b._elements[i];
        return sum;
    }

    /// <summary>Cross product (3D only).</summary>
    public static ToshVector Cross(ToshVector a, ToshVector b)
    {
        if (a.Length != 3 || b.Length != 3)
            throw new InvalidOperationException("Cross product is only defined for 3-dimensional vectors.");
        return new ToshVector([
            a._elements[1] * b._elements[2] - a._elements[2] * b._elements[1],
            a._elements[2] * b._elements[0] - a._elements[0] * b._elements[2],
            a._elements[0] * b._elements[1] - a._elements[1] * b._elements[0],
        ]);
    }

    /// <summary>Euclidean magnitude (L2 norm).</summary>
    public double Magnitude()
    {
        double sum = 0;
        for (int i = 0; i < _elements.Length; i++)
            sum += _elements[i] * _elements[i];
        return Math.Sqrt(sum);
    }

    /// <summary>Returns the unit vector in the same direction.</summary>
    public ToshVector Normalize()
    {
        var mag = Magnitude();
        if (mag == 0)
            throw new InvalidOperationException("Cannot normalize a zero vector.");
        return this / mag;
    }

    /// <summary>Sum of all elements.</summary>
    public double Sum()
    {
        double sum = 0;
        for (int i = 0; i < _elements.Length; i++)
            sum += _elements[i];
        return sum;
    }

    /// <summary>Minimum element value.</summary>
    public double Min() => _elements.Length > 0 ? _elements.Min() : throw new InvalidOperationException("Cannot get min of empty vector.");

    /// <summary>Maximum element value.</summary>
    public double Max() => _elements.Length > 0 ? _elements.Max() : throw new InvalidOperationException("Cannot get max of empty vector.");

    /// <summary>Mean of all elements.</summary>
    public double Mean() => _elements.Length > 0 ? Sum() / _elements.Length : throw new InvalidOperationException("Cannot get mean of empty vector.");

    #endregion

    #region Factories

    public static ToshVector Zeros(int length) => new(new double[length]);

    public static ToshVector Ones(int length)
    {
        var arr = new double[length];
        Array.Fill(arr, 1.0);
        return new ToshVector(arr);
    }

    public static ToshVector Fill(int length, double value)
    {
        var arr = new double[length];
        Array.Fill(arr, value);
        return new ToshVector(arr);
    }

    public static ToshVector Range(double start, double end, double step = 1.0)
    {
        if (step == 0) throw new ArgumentException("Step cannot be zero.", nameof(step));
        var values = new List<double>();
        if (step > 0)
        {
            for (double v = start; v < end; v += step)
                values.Add(v);
        }
        else
        {
            for (double v = start; v > end; v += step)
                values.Add(v);
        }
        return new ToshVector([.. values]);
    }

    public static ToshVector Random(int length, Random? rng = null)
    {
        rng ??= System.Random.Shared;
        var arr = new double[length];
        for (int i = 0; i < length; i++)
            arr[i] = rng.NextDouble();
        return new ToshVector(arr);
    }

    public static ToshVector FromList(IEnumerable<object?> items)
    {
        var values = new List<double>();
        foreach (var item in items)
        {
            values.Add(item switch
            {
                double d => d,
                float f => f,
                int i => i,
                long l => l,
                decimal m => (double)m,
                _ => Convert.ToDouble(item, CultureInfo.InvariantCulture),
            });
        }
        return new ToshVector([.. values]);
    }

    #endregion

    #region Equality & Comparison

    public override bool Equals(object? obj)
    {
        if (obj is not ToshVector other) return false;
        if (_elements.Length != other._elements.Length) return false;
        for (int i = 0; i < _elements.Length; i++)
            if (_elements[i] != other._elements[i]) return false;
        return true;
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(_elements.Length);
        for (int i = 0; i < Math.Min(_elements.Length, 8); i++)
            hash.Add(_elements[i]);
        return hash.ToHashCode();
    }

    public int CompareTo(object? obj)
    {
        if (obj is ToshVector other)
            return Magnitude().CompareTo(other.Magnitude());
        throw new InvalidOperationException("Cannot compare Vector to non-Vector.");
    }

    #endregion

    #region IShellRecordObject

    public string ShellTypeName => "Vector";

    public bool TryGetMember(string name, out object? value, bool includeHidden = false)
    {
        value = name switch
        {
            "Length" or "length" => Length,
            "Magnitude" or "magnitude" => Magnitude(),
            _ => null,
        };
        return value is not null;
    }

    public bool TrySetMember(string name, object? value) => false;

    public IReadOnlyList<KeyValuePair<string, object?>> GetMembers(bool includeHidden = false) =>
    [
        new("Length", Length),
        new("Magnitude", Magnitude()),
    ];

    #endregion

    #region Formatting

    public override string ToString() => ToString(null, CultureInfo.InvariantCulture);

    public string ToString(string? format, IFormatProvider? formatProvider)
    {
        formatProvider ??= CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.Append('[');
        for (int i = 0; i < _elements.Length; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(_elements[i].ToString(format ?? "G", formatProvider));
        }
        sb.Append(']');
        return sb.ToString();
    }

    #endregion

    #region IEnumerable<double>

    public IEnumerator<double> GetEnumerator()
    {
        for (int i = 0; i < _elements.Length; i++)
            yield return _elements[i];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    #endregion

    #region Helpers

    private static void EnsureSameLength(ToshVector a, ToshVector b, string op)
    {
        if (a.Length != b.Length)
            throw new InvalidOperationException(
                $"Cannot apply '{op}' to vectors of different lengths ({a.Length} vs {b.Length}).");
    }

    #endregion
}
