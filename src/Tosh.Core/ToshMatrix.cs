using System.Collections;
using System.Globalization;
using System.Text;

namespace Tosh.Core;

/// <summary>
/// A rectangular numeric matrix for linear algebra and scientific computing.
/// Uses immutable row storage and integrates with the shell's matrix renderer
/// by exposing row sequences rather than record fields.
/// </summary>
public sealed class ToshMatrix : IFormattable, IEnumerable<IReadOnlyList<double>>, IEquatable<ToshMatrix>, IComparable
{
    private const double ZeroTolerance = 1e-12;
    private readonly double[][] _rows;

    public ToshMatrix(IEnumerable<IEnumerable<double>> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        _rows = rows.Select(row => row.ToArray()).ToArray();
        ValidateRectangular(_rows);
    }

    public ToshMatrix(double[][] rows)
        : this(rows.Select(row => (IEnumerable<double>)row))
    {
    }

    public ToshMatrix(double[,] values)
    {
        ArgumentNullException.ThrowIfNull(values);

        var rowCount = values.GetLength(0);
        var columnCount = values.GetLength(1);
        _rows = new double[rowCount][];

        for (var row = 0; row < rowCount; row++)
        {
            var builtRow = new double[columnCount];
            for (var column = 0; column < columnCount; column++)
            {
                builtRow[column] = values[row, column];
            }

            _rows[row] = builtRow;
        }
    }

    public int RowCount => _rows.Length;

    public int ColumnCount => _rows.Length == 0 ? 0 : _rows[0].Length;

    public bool IsSquare => RowCount == ColumnCount;

    public int[] Shape => [RowCount, ColumnCount];

    public double this[int row, int column]
    {
        get
        {
            if (row < 0 || row >= RowCount)
                throw new IndexOutOfRangeException($"Row {row} is out of range for matrix with {RowCount} rows.");
            if (column < 0 || column >= ColumnCount)
                throw new IndexOutOfRangeException($"Column {column} is out of range for matrix with {ColumnCount} columns.");
            return _rows[row][column];
        }
    }

    public ToshVector GetRow(int index)
    {
        if (index < 0 || index >= RowCount)
            throw new IndexOutOfRangeException($"Row {index} is out of range for matrix with {RowCount} rows.");
        return new ToshVector((double[])_rows[index].Clone());
    }

    public ToshVector GetColumn(int index)
    {
        if (index < 0 || index >= ColumnCount)
            throw new IndexOutOfRangeException($"Column {index} is out of range for matrix with {ColumnCount} columns.");

        var values = new double[RowCount];
        for (var row = 0; row < RowCount; row++)
        {
            values[row] = _rows[row][index];
        }

        return new ToshVector(values);
    }

    public double[][] ToJaggedArray() => _rows.Select(row => (double[])row.Clone()).ToArray();

    public double[,] ToArray2D()
    {
        var values = new double[RowCount, ColumnCount];

        for (var row = 0; row < RowCount; row++)
        {
            for (var column = 0; column < ColumnCount; column++)
            {
                values[row, column] = _rows[row][column];
            }
        }

        return values;
    }

    public ToshMatrix Transpose()
    {
        if (RowCount == 0 || ColumnCount == 0)
            return new ToshMatrix(Array.Empty<double[]>());

        var values = new double[ColumnCount][];
        for (var column = 0; column < ColumnCount; column++)
        {
            values[column] = new double[RowCount];
            for (var row = 0; row < RowCount; row++)
            {
                values[column][row] = _rows[row][column];
            }
        }

        return new ToshMatrix(values);
    }

    public double Determinant()
    {
        EnsureSquare("determinant");

        if (RowCount == 0)
            return 1d;

        var values = ToJaggedArray();
        var sign = 1d;

        for (var pivotIndex = 0; pivotIndex < RowCount; pivotIndex++)
        {
            var pivotRow = FindPivotRow(values, pivotIndex, pivotIndex);
            if (pivotRow < 0)
                return 0d;

            if (pivotRow != pivotIndex)
            {
                (values[pivotIndex], values[pivotRow]) = (values[pivotRow], values[pivotIndex]);
                sign = -sign;
            }

            var pivot = values[pivotIndex][pivotIndex];
            if (Math.Abs(pivot) <= ZeroTolerance)
                return 0d;

            for (var row = pivotIndex + 1; row < RowCount; row++)
            {
                var factor = values[row][pivotIndex] / pivot;
                if (Math.Abs(factor) <= ZeroTolerance)
                    continue;

                for (var column = pivotIndex; column < ColumnCount; column++)
                {
                    values[row][column] -= factor * values[pivotIndex][column];
                }
            }
        }

        var determinant = sign;
        for (var index = 0; index < RowCount; index++)
        {
            determinant *= values[index][index];
        }

        return determinant;
    }

    public ToshMatrix Inverse()
    {
        EnsureSquare("inverse");

        if (RowCount == 0)
            return new ToshMatrix(Array.Empty<double[]>());

        var size = RowCount;
        var augmented = new double[size][];

        for (var row = 0; row < size; row++)
        {
            augmented[row] = new double[size * 2];
            Array.Copy(_rows[row], 0, augmented[row], 0, size);
            augmented[row][size + row] = 1d;
        }

        for (var pivotIndex = 0; pivotIndex < size; pivotIndex++)
        {
            var pivotRow = FindPivotRow(augmented, pivotIndex, pivotIndex);
            if (pivotRow < 0)
                throw new InvalidOperationException("Cannot invert a singular matrix.");

            if (pivotRow != pivotIndex)
                (augmented[pivotIndex], augmented[pivotRow]) = (augmented[pivotRow], augmented[pivotIndex]);

            var pivot = augmented[pivotIndex][pivotIndex];
            if (Math.Abs(pivot) <= ZeroTolerance)
                throw new InvalidOperationException("Cannot invert a singular matrix.");

            for (var column = 0; column < size * 2; column++)
            {
                augmented[pivotIndex][column] /= pivot;
            }

            for (var row = 0; row < size; row++)
            {
                if (row == pivotIndex)
                    continue;

                var factor = augmented[row][pivotIndex];
                if (Math.Abs(factor) <= ZeroTolerance)
                    continue;

                for (var column = 0; column < size * 2; column++)
                {
                    augmented[row][column] -= factor * augmented[pivotIndex][column];
                }
            }
        }

        var inverse = new double[size][];
        for (var row = 0; row < size; row++)
        {
            inverse[row] = new double[size];
            Array.Copy(augmented[row], size, inverse[row], 0, size);
        }

        return new ToshMatrix(inverse);
    }

    public double Trace()
    {
        EnsureSquare("trace");

        double sum = 0;
        for (var index = 0; index < RowCount; index++)
        {
            sum += _rows[index][index];
        }

        return sum;
    }

    public double FrobeniusNorm()
    {
        double sum = 0;
        for (var row = 0; row < RowCount; row++)
        {
            for (var column = 0; column < ColumnCount; column++)
            {
                sum += _rows[row][column] * _rows[row][column];
            }
        }

        return Math.Sqrt(sum);
    }

    public double Sum()
    {
        double sum = 0;
        for (var row = 0; row < RowCount; row++)
        {
            for (var column = 0; column < ColumnCount; column++)
            {
                sum += _rows[row][column];
            }
        }

        return sum;
    }

    public double Min()
    {
        EnsureHasCells("min");
        var minimum = _rows[0][0];

        for (var row = 0; row < RowCount; row++)
        {
            for (var column = 0; column < ColumnCount; column++)
            {
                minimum = Math.Min(minimum, _rows[row][column]);
            }
        }

        return minimum;
    }

    public double Max()
    {
        EnsureHasCells("max");
        var maximum = _rows[0][0];

        for (var row = 0; row < RowCount; row++)
        {
            for (var column = 0; column < ColumnCount; column++)
            {
                maximum = Math.Max(maximum, _rows[row][column]);
            }
        }

        return maximum;
    }

    public double Mean()
    {
        EnsureHasCells("mean");
        return Sum() / (RowCount * ColumnCount);
    }

    public static ToshMatrix Zeros(int rows, int columns) => Fill(rows, columns, 0d);

    public static ToshMatrix Ones(int rows, int columns) => Fill(rows, columns, 1d);

    public static ToshMatrix Fill(int rows, int columns, double value)
    {
        if (rows < 0)
            throw new InvalidOperationException("Matrix row count cannot be negative.");
        if (columns < 0)
            throw new InvalidOperationException("Matrix column count cannot be negative.");

        var values = new double[rows][];
        for (var row = 0; row < rows; row++)
        {
            values[row] = new double[columns];
            Array.Fill(values[row], value);
        }

        return new ToshMatrix(values);
    }

    public static ToshMatrix Identity(int size)
    {
        if (size < 0)
            throw new InvalidOperationException("Matrix size cannot be negative.");

        var values = new double[size][];
        for (var row = 0; row < size; row++)
        {
            values[row] = new double[size];
            values[row][row] = 1d;
        }

        return new ToshMatrix(values);
    }

    public static ToshMatrix Random(int rows, int columns, Random? rng = null)
    {
        rng ??= System.Random.Shared;
        var values = new double[rows][];

        for (var row = 0; row < rows; row++)
        {
            values[row] = new double[columns];
            for (var column = 0; column < columns; column++)
            {
                values[row][column] = rng.NextDouble();
            }
        }

        return new ToshMatrix(values);
    }

    public static ToshMatrix FromValue(object? value)
    {
        return value switch
        {
            ToshMatrix matrix => new ToshMatrix(matrix.ToJaggedArray()),
            double[,] values => new ToshMatrix(values),
            Array array when array.Rank == 2 => FromArray(array),
            _ when TryGetSequenceItems(value, out var items) => FromRows(items),
            _ => throw new InvalidOperationException($"Expected a matrix-compatible value, got {value?.GetType().Name ?? "null"}."),
        };
    }

    public static ToshMatrix FromRows(IEnumerable<object?> rowsOrValues)
    {
        ArgumentNullException.ThrowIfNull(rowsOrValues);

        var items = rowsOrValues.ToArray();
        if (items.Length == 0)
            return new ToshMatrix(Array.Empty<double[]>());

        var parsedRows = new List<double[]>(items.Length);
        var allRows = true;
        var anyRows = false;

        foreach (var item in items)
        {
            if (TryConvertToRow(item, out var row))
            {
                parsedRows.Add(row);
                anyRows = true;
            }
            else
            {
                allRows = false;
            }
        }

        if (allRows)
            return new ToshMatrix(parsedRows);

        if (!anyRows)
            return new ToshMatrix([items.Select(ConvertToDouble).ToArray()]);

        throw new InvalidOperationException("Matrix construction expects either row sequences or scalar values, but not a mix of both.");
    }

    public static ToshMatrix Multiply(ToshMatrix left, ToshMatrix right)
    {
        EnsureInnerDimensionMatch(left, right, "multiply");

        var result = new double[left.RowCount][];
        for (var row = 0; row < left.RowCount; row++)
        {
            result[row] = new double[right.ColumnCount];
            for (var column = 0; column < right.ColumnCount; column++)
            {
                double sum = 0;
                for (var inner = 0; inner < left.ColumnCount; inner++)
                {
                    sum += left._rows[row][inner] * right._rows[inner][column];
                }

                result[row][column] = sum;
            }
        }

        return new ToshMatrix(result);
    }

    public static ToshVector Multiply(ToshMatrix matrix, ToshVector vector)
    {
        if (matrix.ColumnCount != vector.Length)
            throw new InvalidOperationException($"Cannot multiply a {matrix.RowCount}x{matrix.ColumnCount} matrix by a vector of length {vector.Length}.");

        var result = new double[matrix.RowCount];
        for (var row = 0; row < matrix.RowCount; row++)
        {
            double sum = 0;
            for (var column = 0; column < matrix.ColumnCount; column++)
            {
                sum += matrix._rows[row][column] * vector[column];
            }

            result[row] = sum;
        }

        return new ToshVector(result);
    }

    public static ToshVector Multiply(ToshVector vector, ToshMatrix matrix)
    {
        if (vector.Length != matrix.RowCount)
            throw new InvalidOperationException($"Cannot multiply a vector of length {vector.Length} by a {matrix.RowCount}x{matrix.ColumnCount} matrix.");

        var result = new double[matrix.ColumnCount];
        for (var column = 0; column < matrix.ColumnCount; column++)
        {
            double sum = 0;
            for (var row = 0; row < matrix.RowCount; row++)
            {
                sum += vector[row] * matrix._rows[row][column];
            }

            result[column] = sum;
        }

        return new ToshVector(result);
    }

    public static ToshMatrix Hadamard(ToshMatrix left, ToshMatrix right)
    {
        EnsureSameShape(left, right, "hadamard");
        var values = new double[left.RowCount][];

        for (var row = 0; row < left.RowCount; row++)
        {
            values[row] = new double[left.ColumnCount];
            for (var column = 0; column < left.ColumnCount; column++)
            {
                values[row][column] = left._rows[row][column] * right._rows[row][column];
            }
        }

        return new ToshMatrix(values);
    }

    public static ToshMatrix operator +(ToshMatrix left, ToshMatrix right)
    {
        EnsureSameShape(left, right, "+");
        return ElementWise(left, right, (lhs, rhs) => lhs + rhs);
    }

    public static ToshMatrix operator -(ToshMatrix left, ToshMatrix right)
    {
        EnsureSameShape(left, right, "-");
        return ElementWise(left, right, (lhs, rhs) => lhs - rhs);
    }

    public static ToshMatrix operator /(ToshMatrix left, ToshMatrix right)
    {
        EnsureSameShape(left, right, "/");
        return ElementWise(left, right, (lhs, rhs) =>
        {
            if (Math.Abs(rhs) <= ZeroTolerance)
                throw new DivideByZeroException("Cannot divide matrix by a matrix containing zero.");
            return lhs / rhs;
        });
    }

    public static ToshMatrix operator *(ToshMatrix matrix, double scalar)
    {
        var values = new double[matrix.RowCount][];
        for (var row = 0; row < matrix.RowCount; row++)
        {
            values[row] = new double[matrix.ColumnCount];
            for (var column = 0; column < matrix.ColumnCount; column++)
            {
                values[row][column] = matrix._rows[row][column] * scalar;
            }
        }

        return new ToshMatrix(values);
    }

    public static ToshMatrix operator *(double scalar, ToshMatrix matrix) => matrix * scalar;

    public static ToshMatrix operator /(ToshMatrix matrix, double scalar)
    {
        if (Math.Abs(scalar) <= ZeroTolerance)
            throw new DivideByZeroException("Cannot divide matrix by zero.");

        var values = new double[matrix.RowCount][];
        for (var row = 0; row < matrix.RowCount; row++)
        {
            values[row] = new double[matrix.ColumnCount];
            for (var column = 0; column < matrix.ColumnCount; column++)
            {
                values[row][column] = matrix._rows[row][column] / scalar;
            }
        }

        return new ToshMatrix(values);
    }

    public static ToshMatrix operator -(ToshMatrix matrix) => -1d * matrix;

    public override bool Equals(object? obj) => Equals(obj as ToshMatrix);

    public bool Equals(ToshMatrix? other)
    {
        if (other is null || RowCount != other.RowCount || ColumnCount != other.ColumnCount)
            return false;

        for (var row = 0; row < RowCount; row++)
        {
            for (var column = 0; column < ColumnCount; column++)
            {
                if (_rows[row][column] != other._rows[row][column])
                    return false;
            }
        }

        return true;
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(RowCount);
        hash.Add(ColumnCount);

        for (var row = 0; row < Math.Min(RowCount, 4); row++)
        {
            for (var column = 0; column < Math.Min(ColumnCount, 4); column++)
            {
                hash.Add(_rows[row][column]);
            }
        }

        return hash.ToHashCode();
    }

    public int CompareTo(object? obj)
    {
        if (obj is ToshMatrix other)
            return FrobeniusNorm().CompareTo(other.FrobeniusNorm());

        throw new InvalidOperationException("Cannot compare Matrix to non-Matrix.");
    }

    public override string ToString() => ToString(null, CultureInfo.InvariantCulture);

    public string ToString(string? format, IFormatProvider? formatProvider)
    {
        formatProvider ??= CultureInfo.InvariantCulture;
        var builder = new StringBuilder();
        builder.Append('[');

        for (var row = 0; row < RowCount; row++)
        {
            if (row > 0)
                builder.Append(", ");

            builder.Append('[');
            for (var column = 0; column < ColumnCount; column++)
            {
                if (column > 0)
                    builder.Append(", ");

                builder.Append(_rows[row][column].ToString(format ?? "G", formatProvider));
            }

            builder.Append(']');
        }

        builder.Append(']');
        return builder.ToString();
    }

    public IEnumerator<IReadOnlyList<double>> GetEnumerator()
    {
        for (var row = 0; row < RowCount; row++)
        {
            yield return Array.AsReadOnly(_rows[row]);
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private static ToshMatrix ElementWise(ToshMatrix left, ToshMatrix right, Func<double, double, double> operation)
    {
        var values = new double[left.RowCount][];

        for (var row = 0; row < left.RowCount; row++)
        {
            values[row] = new double[left.ColumnCount];
            for (var column = 0; column < left.ColumnCount; column++)
            {
                values[row][column] = operation(left._rows[row][column], right._rows[row][column]);
            }
        }

        return new ToshMatrix(values);
    }

    private static ToshMatrix FromArray(Array array)
    {
        var rowCount = array.GetLength(0);
        var columnCount = array.GetLength(1);
        var values = new double[rowCount][];

        for (var row = 0; row < rowCount; row++)
        {
            values[row] = new double[columnCount];
            for (var column = 0; column < columnCount; column++)
            {
                values[row][column] = ConvertToDouble(array.GetValue(row, column));
            }
        }

        return new ToshMatrix(values);
    }

    private static bool TryConvertToRow(object? value, out double[] row)
    {
        if (value is ToshVector vector)
        {
            row = vector.ToArray();
            return true;
        }

        if (value is null ||
            value is string ||
            value is ShellTextLine ||
            value is IDictionary ||
            ShellRecordUtilities.IsRecordLike(value) ||
            value is not IEnumerable enumerable)
        {
            row = Array.Empty<double>();
            return false;
        }

        var items = enumerable.Cast<object?>().ToArray();
        if (items.Any(IsNestedSequenceValue))
        {
            row = Array.Empty<double>();
            return false;
        }

        row = items.Select(ConvertToDouble).ToArray();
        return true;
    }

    private static bool TryGetSequenceItems(object? value, out IReadOnlyList<object?> items)
    {
        items = Array.Empty<object?>();

        if (value is null ||
            value is string ||
            value is ShellTextLine ||
            value is IDictionary ||
            ShellRecordUtilities.IsRecordLike(value) ||
            value is not IEnumerable enumerable)
        {
            return false;
        }

        items = enumerable.Cast<object?>().ToArray();
        return true;
    }

    private static bool IsNestedSequenceValue(object? value)
    {
        return value is not null &&
               value is not string &&
               value is not ShellTextLine &&
               value is not IDictionary &&
               !ShellRecordUtilities.IsRecordLike(value) &&
               value is IEnumerable;
    }

    private static double ConvertToDouble(object? value)
    {
        return value switch
        {
            double d => d,
            float f => f,
            int i => i,
            long l => l,
            decimal m => (double)m,
            _ => Convert.ToDouble(value, CultureInfo.InvariantCulture),
        };
    }

    private static int FindPivotRow(double[][] values, int startRow, int column)
    {
        var pivotRow = -1;
        var bestMagnitude = ZeroTolerance;

        for (var row = startRow; row < values.Length; row++)
        {
            var magnitude = Math.Abs(values[row][column]);
            if (magnitude > bestMagnitude)
            {
                bestMagnitude = magnitude;
                pivotRow = row;
            }
        }

        return pivotRow;
    }

    private void EnsureSquare(string operation)
    {
        if (!IsSquare)
            throw new InvalidOperationException($"Matrix {operation} requires a square matrix, got {RowCount}x{ColumnCount}.");
    }

    private void EnsureHasCells(string operation)
    {
        if (RowCount == 0 || ColumnCount == 0)
            throw new InvalidOperationException($"Cannot compute matrix {operation} for an empty matrix.");
    }

    private static void EnsureSameShape(ToshMatrix left, ToshMatrix right, string operation)
    {
        if (left.RowCount != right.RowCount || left.ColumnCount != right.ColumnCount)
        {
            throw new InvalidOperationException(
                $"Cannot apply '{operation}' to matrices of different shapes ({left.RowCount}x{left.ColumnCount} vs {right.RowCount}x{right.ColumnCount}).");
        }
    }

    private static void EnsureInnerDimensionMatch(ToshMatrix left, ToshMatrix right, string operation)
    {
        if (left.ColumnCount != right.RowCount)
        {
            throw new InvalidOperationException(
                $"Cannot {operation} a {left.RowCount}x{left.ColumnCount} matrix by a {right.RowCount}x{right.ColumnCount} matrix.");
        }
    }

    private static void ValidateRectangular(double[][] rows)
    {
        if (rows.Length == 0)
            return;

        var columnCount = rows[0].Length;
        for (var row = 1; row < rows.Length; row++)
        {
            if (rows[row].Length != columnCount)
            {
                throw new InvalidOperationException("Matrix rows must all have the same length.");
            }
        }
    }
}
