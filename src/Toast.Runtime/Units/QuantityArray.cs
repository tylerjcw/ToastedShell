using System.Collections;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace Tosh.Runtime.Units;

/// <summary>
/// A high-performance, contiguous, SIMD-accelerated array of physical quantities.
/// Eliminates per-element object boxing by storing a single flat <c>double[]</c>
/// buffer alongside a shared <see cref="UnitExpression"/>, unit symbol, and semantic kind.
/// </summary>
public sealed class QuantityArray : IReadOnlyList<Quantity>, IShellRecordObject, IFormattable, IEquatable<QuantityArray>
{
    private readonly double[] _magnitudes;
    private readonly UnitConversion _conversion;

    public UnitExpression Dimension { get; }
    public string UnitSymbol { get; }
    public string? SemanticKind { get; }

    public int Count => _magnitudes.Length;
    public int Length => _magnitudes.Length;

    public Quantity this[int index]
    {
        get
        {
            if (index < 0 || index >= _magnitudes.Length)
            {
                throw new IndexOutOfRangeException(
                    $"Index {index} is out of range for QuantityArray of length {_magnitudes.Length}.");
            }
            return Quantity.FromParsed(_magnitudes[index], Dimension, UnitSymbol);
        }
    }

    public double GetMagnitudeAt(int index) => _magnitudes[index];

    public double GetBaseValueAt(int index) => _conversion.ToBase(_magnitudes[index]);

    public ReadOnlySpan<double> Magnitudes => _magnitudes;

    public double[] ToArray() => (double[])_magnitudes.Clone();

    public QuantityArray(
        double[] magnitudes,
        UnitExpression dimension,
        string unitSymbol,
        string? semanticKind = null)
    {
        ArgumentNullException.ThrowIfNull(magnitudes);
        ArgumentNullException.ThrowIfNull(dimension);
        ArgumentNullException.ThrowIfNull(unitSymbol);

        _magnitudes = magnitudes;
        Dimension = dimension;
        UnitSymbol = unitSymbol;

        if (dimension.IsDimensionless && string.IsNullOrEmpty(unitSymbol))
        {
            _conversion = UnitConversion.Identity;
            SemanticKind = semanticKind;
            return;
        }

        if (!UnitExpressionParser.TryParseConversion(
                unitSymbol,
                out var conversion,
                out var parsedDim,
                out var normalizedSymbol) ||
            parsedDim != dimension)
        {
            throw new ArgumentException(
                $"Unit '{unitSymbol}' does not describe dimension '{dimension}'.",
                nameof(unitSymbol));
        }

        _conversion = conversion;
        UnitSymbol = normalizedSymbol;

        if (semanticKind is not null)
        {
            SemanticKind = semanticKind;
        }
        else if (UnitRegistry.Instance.TryResolve(unitSymbol) is { } def)
        {
            SemanticKind = def.SemanticKind;
        }
    }

    public QuantityArray(IEnumerable<double> magnitudes, string unitSymbol)
    {
        ArgumentNullException.ThrowIfNull(magnitudes);
        ArgumentNullException.ThrowIfNull(unitSymbol);

        if (!UnitExpressionParser.TryParseConversion(
                unitSymbol,
                out var conversion,
                out var dimension,
                out var normalizedSymbol))
        {
            throw new ArgumentException(
                $"Unknown or invalid unit expression: '{unitSymbol}'.",
                nameof(unitSymbol));
        }

        _magnitudes = magnitudes.ToArray();
        Dimension = dimension;
        UnitSymbol = normalizedSymbol;
        _conversion = conversion;

        if (UnitRegistry.Instance.TryResolve(normalizedSymbol) is { } def)
        {
            SemanticKind = def.SemanticKind;
        }
    }

    public QuantityArray(IEnumerable<Quantity> quantities)
    {
        ArgumentNullException.ThrowIfNull(quantities);

        var list = quantities.ToList();
        if (list.Count == 0)
        {
            _magnitudes = [];
            Dimension = UnitExpression.Dimensionless;
            UnitSymbol = string.Empty;
            _conversion = UnitConversion.Identity;
            return;
        }

        var first = list[0];
        Dimension = first.Dimension;
        UnitSymbol = first.UnitSymbol;
        SemanticKind = first.SemanticKind;
        _conversion = first.Conversion;

        _magnitudes = new double[list.Count];
        for (int i = 0; i < list.Count; i++)
        {
            var q = list[i];
            if (q.Dimension != Dimension)
            {
                throw new InvalidOperationException(
                    $"Cannot construct QuantityArray: element at index {i} has dimension '{q.Dimension}', expected '{Dimension}'.");
            }

            if (!string.Equals(q.SemanticKind, SemanticKind, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Cannot construct QuantityArray: semantic kind mismatch ('{q.SemanticKind}' vs '{SemanticKind}').");
            }

            _magnitudes[i] = string.Equals(q.UnitSymbol, UnitSymbol, StringComparison.Ordinal)
                ? q.Magnitude
                : q.To(UnitSymbol).Magnitude;
        }
    }

    public QuantityArray Slice(int start, int length)
    {
        if (start < 0 || start > _magnitudes.Length)
            throw new ArgumentOutOfRangeException(nameof(start));
        if (length < 0 || start + length > _magnitudes.Length)
            throw new ArgumentOutOfRangeException(nameof(length));

        var sliced = new double[length];
        Array.Copy(_magnitudes, start, sliced, 0, length);
        return new QuantityArray(sliced, Dimension, UnitSymbol, SemanticKind);
    }

    #region Element-wise Vector Arithmetic

    public static QuantityArray operator +(QuantityArray left, QuantityArray right)
    {
        EnsureCompatible(left, right, "+");
        var result = new double[left.Length];

        if (string.Equals(left.UnitSymbol, right.UnitSymbol, StringComparison.Ordinal))
        {
            SimdAdd(left._magnitudes, right._magnitudes, result);
            return new QuantityArray(result, left.Dimension, left.UnitSymbol, left.SemanticKind);
        }

        var convertedRight = right.To(left.UnitSymbol);
        SimdAdd(left._magnitudes, convertedRight._magnitudes, result);
        return new QuantityArray(result, left.Dimension, left.UnitSymbol, left.SemanticKind);
    }

    public static QuantityArray operator -(QuantityArray left, QuantityArray right)
    {
        EnsureCompatible(left, right, "-");
        var result = new double[left.Length];

        if (string.Equals(left.UnitSymbol, right.UnitSymbol, StringComparison.Ordinal))
        {
            SimdSubtract(left._magnitudes, right._magnitudes, result);
            return new QuantityArray(result, left.Dimension, left.UnitSymbol, left.SemanticKind);
        }

        var convertedRight = right.To(left.UnitSymbol);
        SimdSubtract(left._magnitudes, convertedRight._magnitudes, result);
        return new QuantityArray(result, left.Dimension, left.UnitSymbol, left.SemanticKind);
    }

    public static QuantityArray operator -(QuantityArray array)
    {
        var result = new double[array.Length];
        SimdNegate(array._magnitudes, result);
        return new QuantityArray(result, array.Dimension, array.UnitSymbol, array.SemanticKind);
    }

    public static QuantityArray operator *(QuantityArray array, double scalar)
    {
        var result = new double[array.Length];
        SimdMultiplyScalar(array._magnitudes, scalar, result);
        return new QuantityArray(result, array.Dimension, array.UnitSymbol, array.SemanticKind);
    }

    public static QuantityArray operator *(double scalar, QuantityArray array) => array * scalar;

    public static QuantityArray operator /(QuantityArray array, double scalar)
    {
        if (scalar == 0.0) throw new DivideByZeroException("Cannot divide QuantityArray by zero scalar.");
        var result = new double[array.Length];
        SimdDivideScalar(array._magnitudes, scalar, result);
        return new QuantityArray(result, array.Dimension, array.UnitSymbol, array.SemanticKind);
    }

    public static QuantityArray operator *(QuantityArray left, QuantityArray right)
    {
        EnsureSameLength(left, right, "*");
        var newDim = left.Dimension.Multiply(right.Dimension);
        var newSymbol = CombineSymbols(left.UnitSymbol, right.UnitSymbol, "*");
        var result = new double[left.Length];
        SimdMultiply(left._magnitudes, right._magnitudes, result);
        return new QuantityArray(result, newDim, newSymbol);
    }

    public static QuantityArray operator /(QuantityArray left, QuantityArray right)
    {
        EnsureSameLength(left, right, "/");
        var newDim = left.Dimension.Divide(right.Dimension);
        var newSymbol = CombineSymbols(left.UnitSymbol, right.UnitSymbol, "/");
        var result = new double[left.Length];
        SimdDivide(left._magnitudes, right._magnitudes, result);
        return new QuantityArray(result, newDim, newSymbol);
    }

    public static QuantityArray operator *(QuantityArray array, Quantity q)
    {
        var newDim = array.Dimension.Multiply(q.Dimension);
        var newSymbol = CombineSymbols(array.UnitSymbol, q.UnitSymbol, "*");
        var result = new double[array.Length];
        SimdMultiplyScalar(array._magnitudes, q.Magnitude, result);
        return new QuantityArray(result, newDim, newSymbol);
    }

    public static QuantityArray operator *(Quantity q, QuantityArray array) => array * q;

    public static QuantityArray operator /(QuantityArray array, Quantity q)
    {
        if (q.Magnitude == 0.0) throw new DivideByZeroException("Cannot divide QuantityArray by zero quantity.");
        var newDim = array.Dimension.Divide(q.Dimension);
        var newSymbol = CombineSymbols(array.UnitSymbol, q.UnitSymbol, "/");
        var result = new double[array.Length];
        SimdDivideScalar(array._magnitudes, q.Magnitude, result);
        return new QuantityArray(result, newDim, newSymbol);
    }

    public static QuantityArray operator +(QuantityArray array, Quantity q)
    {
        if (array.Dimension != q.Dimension)
        {
            throw new InvalidOperationException(
                $"Cannot add scalar quantity '{q}' with dimension '{q.Dimension}' to QuantityArray with dimension '{array.Dimension}'.");
        }

        if (!string.Equals(array.SemanticKind, q.SemanticKind, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Cannot add scalar quantity of semantic kind '{q.SemanticKind}' to QuantityArray of semantic kind '{array.SemanticKind}'.");
        }

        var scalarInUnit = q.To(array.UnitSymbol).Magnitude;
        var result = new double[array.Length];
        SimdAddScalar(array._magnitudes, scalarInUnit, result);
        return new QuantityArray(result, array.Dimension, array.UnitSymbol, array.SemanticKind);
    }

    public static QuantityArray operator +(Quantity q, QuantityArray array) => array + q;

    public static QuantityArray operator -(QuantityArray array, Quantity q)
    {
        return array + (-q);
    }

    #endregion

    #region Reductions and Conversions

    public Quantity Sum()
    {
        if (_magnitudes.Length == 0) return Quantity.FromParsed(0.0, Dimension, UnitSymbol);
        double sum = 0.0;
        for (int i = 0; i < _magnitudes.Length; i++)
        {
            sum += _magnitudes[i];
        }
        return Quantity.FromParsed(sum, Dimension, UnitSymbol);
    }

    public Quantity Mean()
    {
        if (_magnitudes.Length == 0)
        {
            throw new InvalidOperationException("Cannot compute mean of empty QuantityArray.");
        }
        return Quantity.FromParsed(Sum().Magnitude / _magnitudes.Length, Dimension, UnitSymbol);
    }

    public Quantity Min()
    {
        if (_magnitudes.Length == 0)
        {
            throw new InvalidOperationException("Cannot compute min of empty QuantityArray.");
        }
        double min = _magnitudes[0];
        for (int i = 1; i < _magnitudes.Length; i++)
        {
            if (_magnitudes[i] < min) min = _magnitudes[i];
        }
        return Quantity.FromParsed(min, Dimension, UnitSymbol);
    }

    public Quantity Max()
    {
        if (_magnitudes.Length == 0)
        {
            throw new InvalidOperationException("Cannot compute max of empty QuantityArray.");
        }
        double max = _magnitudes[0];
        for (int i = 1; i < _magnitudes.Length; i++)
        {
            if (_magnitudes[i] > max) max = _magnitudes[i];
        }
        return Quantity.FromParsed(max, Dimension, UnitSymbol);
    }

    public QuantityArray To(string targetUnit)
    {
        if (string.Equals(UnitSymbol, targetUnit, StringComparison.Ordinal))
        {
            return this;
        }

        if (!UnitExpressionParser.TryParseConversion(
                targetUnit,
                out var targetConversion,
                out var targetDimension,
                out var normTargetSymbol))
        {
            throw new ArgumentException($"Unknown target unit: '{targetUnit}'.", nameof(targetUnit));
        }

        if (targetDimension != Dimension)
        {
            throw new InvalidOperationException(
                $"Cannot convert QuantityArray from '{UnitSymbol}' ({Dimension}) to '{targetUnit}' ({targetDimension}).");
        }

        var result = new double[_magnitudes.Length];
        var factor = _conversion.ToBaseFactor / targetConversion.ToBaseFactor;

        SimdMultiplyScalar(_magnitudes, factor, result);
        return new QuantityArray(result, Dimension, normTargetSymbol, SemanticKind);
    }

    public ToshVector ToVector() => new ToshVector(_magnitudes);

    #endregion

    #region SIMD Helpers

    private static void SimdAdd(double[] a, double[] b, double[] result)
    {
        int i = 0;
        int count = a.Length;
        int simdCount = Vector<double>.Count;
        int end = count - (count % simdCount);

        for (; i < end; i += simdCount)
        {
            var va = new Vector<double>(a, i);
            var vb = new Vector<double>(b, i);
            (va + vb).CopyTo(result, i);
        }

        for (; i < count; i++)
        {
            result[i] = a[i] + b[i];
        }
    }

    private static void SimdSubtract(double[] a, double[] b, double[] result)
    {
        int i = 0;
        int count = a.Length;
        int simdCount = Vector<double>.Count;
        int end = count - (count % simdCount);

        for (; i < end; i += simdCount)
        {
            var va = new Vector<double>(a, i);
            var vb = new Vector<double>(b, i);
            (va - vb).CopyTo(result, i);
        }

        for (; i < count; i++)
        {
            result[i] = a[i] - b[i];
        }
    }

    private static void SimdMultiply(double[] a, double[] b, double[] result)
    {
        int i = 0;
        int count = a.Length;
        int simdCount = Vector<double>.Count;
        int end = count - (count % simdCount);

        for (; i < end; i += simdCount)
        {
            var va = new Vector<double>(a, i);
            var vb = new Vector<double>(b, i);
            (va * vb).CopyTo(result, i);
        }

        for (; i < count; i++)
        {
            result[i] = a[i] * b[i];
        }
    }

    private static void SimdDivide(double[] a, double[] b, double[] result)
    {
        int i = 0;
        int count = a.Length;
        int simdCount = Vector<double>.Count;
        int end = count - (count % simdCount);

        for (; i < end; i += simdCount)
        {
            var va = new Vector<double>(a, i);
            var vb = new Vector<double>(b, i);
            (va / vb).CopyTo(result, i);
        }

        for (; i < count; i++)
        {
            if (b[i] == 0.0) throw new DivideByZeroException($"Division by zero at element {i}.");
            result[i] = a[i] / b[i];
        }
    }

    private static void SimdMultiplyScalar(double[] a, double scalar, double[] result)
    {
        int i = 0;
        int count = a.Length;
        int simdCount = Vector<double>.Count;
        int end = count - (count % simdCount);
        var vs = new Vector<double>(scalar);

        for (; i < end; i += simdCount)
        {
            var va = new Vector<double>(a, i);
            (va * vs).CopyTo(result, i);
        }

        for (; i < count; i++)
        {
            result[i] = a[i] * scalar;
        }
    }

    private static void SimdDivideScalar(double[] a, double scalar, double[] result)
    {
        int i = 0;
        int count = a.Length;
        int simdCount = Vector<double>.Count;
        int end = count - (count % simdCount);
        var vs = new Vector<double>(scalar);

        for (; i < end; i += simdCount)
        {
            var va = new Vector<double>(a, i);
            (va / vs).CopyTo(result, i);
        }

        for (; i < count; i++)
        {
            result[i] = a[i] / scalar;
        }
    }

    private static void SimdAddScalar(double[] a, double scalar, double[] result)
    {
        int i = 0;
        int count = a.Length;
        int simdCount = Vector<double>.Count;
        int end = count - (count % simdCount);
        var vs = new Vector<double>(scalar);

        for (; i < end; i += simdCount)
        {
            var va = new Vector<double>(a, i);
            (va + vs).CopyTo(result, i);
        }

        for (; i < count; i++)
        {
            result[i] = a[i] + scalar;
        }
    }

    private static void SimdNegate(double[] a, double[] result)
    {
        int i = 0;
        int count = a.Length;
        int simdCount = Vector<double>.Count;
        int end = count - (count % simdCount);

        for (; i < end; i += simdCount)
        {
            var va = new Vector<double>(a, i);
            (-va).CopyTo(result, i);
        }

        for (; i < count; i++)
        {
            result[i] = -a[i];
        }
    }

    #endregion

    #region Internal Checks

    private static void EnsureCompatible(QuantityArray left, QuantityArray right, string op)
    {
        EnsureSameLength(left, right, op);

        if (left.Dimension != right.Dimension)
        {
            throw new InvalidOperationException(
                $"Cannot perform '{op}' on QuantityArrays with mismatched dimensions: '{left.Dimension}' vs '{right.Dimension}'.");
        }

        if (!string.Equals(left.SemanticKind, right.SemanticKind, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Cannot perform '{op}' across different semantic kinds: '{left.SemanticKind}' vs '{right.SemanticKind}'.");
        }
    }

    private static void EnsureSameLength(QuantityArray left, QuantityArray right, string op)
    {
        if (left.Length != right.Length)
        {
            throw new InvalidOperationException(
                $"Cannot perform '{op}' on QuantityArrays of different lengths ({left.Length} vs {right.Length}).");
        }
    }

    private static string CombineSymbols(string left, string right, string op)
    {
        if (string.IsNullOrEmpty(left)) return right;
        if (string.IsNullOrEmpty(right)) return left;
        return op == "*" ? $"{left}*{right}" : $"{left}/{right}";
    }

    #endregion

    #region IShellRecordObject

    public string ShellTypeName => "QuantityArray";

    public bool TryGetMember(string name, out object? value, bool includeHidden = false)
    {
        value = name switch
        {
            "Length" or "length" or "Count" or "count" => Length,
            "Dimension" or "dimension" => Dimension,
            "Unit" or "unit" or "UnitSymbol" or "unitSymbol" => UnitSymbol,
            "SemanticKind" or "semanticKind" => SemanticKind,
            "Magnitudes" or "magnitudes" or "Values" or "values" => ToVector(),
            "Sum" or "sum" => Sum(),
            "Mean" or "mean" => Mean(),
            "Min" or "min" => Min(),
            "Max" or "max" => Max(),
            _ => null,
        };
        return value is not null;
    }

    public bool TrySetMember(string name, object? value) => false;

    public IReadOnlyList<KeyValuePair<string, object?>> GetMembers(bool includeHidden = false) =>
    [
        new("Length", Length),
        new("Dimension", Dimension),
        new("Unit", UnitSymbol),
        new("SemanticKind", SemanticKind),
        new("Magnitudes", ToVector()),
    ];

    #endregion

    #region IEnumerable & Formatting

    public IEnumerator<Quantity> GetEnumerator()
    {
        for (int i = 0; i < _magnitudes.Length; i++)
        {
            yield return this[i];
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public override string ToString() => ToString(null, CultureInfo.InvariantCulture);

    public string ToString(string? format, IFormatProvider? formatProvider)
    {
        formatProvider ??= CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.Append('[');
        var limit = Math.Min(_magnitudes.Length, 10);
        for (int i = 0; i < limit; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(_magnitudes[i].ToString(format ?? "G", formatProvider));
        }
        if (_magnitudes.Length > 10)
        {
            sb.Append($", ... ({_magnitudes.Length - 10} more)");
        }
        sb.Append(']');
        if (!string.IsNullOrEmpty(UnitSymbol))
        {
            sb.Append('`').Append(UnitSymbol);
        }
        return sb.ToString();
    }

    public bool Equals(QuantityArray? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        if (Length != other.Length || Dimension != other.Dimension) return false;

        for (int i = 0; i < _magnitudes.Length; i++)
        {
            if (Math.Abs(GetBaseValueAt(i) - other.GetBaseValueAt(i)) > 1e-9)
            {
                return false;
            }
        }
        return true;
    }

    public override bool Equals(object? obj) => Equals(obj as QuantityArray);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Dimension);
        hash.Add(Length);
        if (_magnitudes.Length > 0)
        {
            hash.Add(_magnitudes[0]);
        }
        return hash.ToHashCode();
    }

    #endregion
}
