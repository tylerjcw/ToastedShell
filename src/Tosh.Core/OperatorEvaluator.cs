using System.Globalization;

namespace Tosh.Core;

public static class OperatorEvaluator
{
    public static object? EvaluateBinary(object? left, string @operator, object? right)
    {
        return @operator switch
        {
            "+" => Add(left, right),
            "-" => Subtract(left, right),
            "*" => Multiply(left, right),
            "/" => Divide(left, right),
            "==" => AreEqual(left, right),
            "!=" => !AreEqual(left, right),
            ">" => EvaluateOrderedComparison(left, right, nullable: false, comparison => comparison > 0),
            ">=" => EvaluateOrderedComparison(left, right, nullable: false, comparison => comparison >= 0),
            "<" => EvaluateOrderedComparison(left, right, nullable: false, comparison => comparison < 0),
            "<=" => EvaluateOrderedComparison(left, right, nullable: false, comparison => comparison <= 0),
            "and" => ToBoolean(left) && ToBoolean(right),
            "or" => ToBoolean(left) || ToBoolean(right),
            _ => throw new InvalidOperationException($"Unsupported operator '{@operator}'."),
        };
    }

    public static bool Matches(object? actual, string @operator, object? expected, bool nullable)
    {
        return @operator switch
        {
            "==" or "eq" => AreEqual(actual, expected),
            "!=" or "ne" => !AreEqual(actual, expected),
            "contains" => Contains(actual, expected),
            "starts-with" => StartsWith(actual, expected),
            "ends-with" => EndsWith(actual, expected),
            ">" => EvaluateOrderedComparison(actual, expected, nullable, comparison => comparison > 0),
            ">=" => EvaluateOrderedComparison(actual, expected, nullable, comparison => comparison >= 0),
            "<" => EvaluateOrderedComparison(actual, expected, nullable, comparison => comparison < 0),
            "<=" => EvaluateOrderedComparison(actual, expected, nullable, comparison => comparison <= 0),
            _ => throw new InvalidOperationException($"Unsupported operator '{@operator}'. Supported operators: ==, !=, >, >=, <, <=, contains, starts-with, ends-with."),
        };
    }

    public static bool AreEqual(object? actual, object? expected)
    {
        if (actual is null || expected is null)
        {
            return Equals(actual, expected);
        }

        if (TypeConversion.TryConvert(expected, actual.GetType(), out var convertedExpected))
        {
            return Equals(actual, convertedExpected);
        }

        if (TypeConversion.TryConvert(actual, expected.GetType(), out var convertedActual))
        {
            return Equals(convertedActual, expected);
        }

        return Equals(actual, expected);
    }

    public static bool EvaluateOrderedComparison(object? actual, object? expected, bool nullable, Func<int, bool> predicate)
    {
        if (actual is null || expected is null)
        {
            if (nullable)
            {
                return false;
            }

            throw new InvalidOperationException("Ordered comparisons require non-null values.");
        }

        if (actual is IComparable comparable &&
            TypeConversion.TryConvert(expected, actual.GetType(), out var convertedExpected))
        {
            return predicate(comparable.CompareTo(convertedExpected));
        }

        throw new InvalidOperationException($"Values of type '{actual.GetType().FullName}' cannot be compared with '{expected.GetType().FullName}'.");
    }

    private static bool Contains(object? actual, object? expected)
    {
        return actual?.ToString()?.Contains(expected?.ToString() ?? string.Empty, StringComparison.Ordinal) == true;
    }

    private static bool StartsWith(object? actual, object? expected)
    {
        return actual?.ToString()?.StartsWith(expected?.ToString() ?? string.Empty, StringComparison.Ordinal) == true;
    }

    private static bool EndsWith(object? actual, object? expected)
    {
        return actual?.ToString()?.EndsWith(expected?.ToString() ?? string.Empty, StringComparison.Ordinal) == true;
    }

    private static object? Add(object? left, object? right)
    {
        if (left is null || right is null)
        {
            throw new InvalidOperationException("The '+' operator requires non-null operands.");
        }

        if (left is string leftText)
        {
            return leftText + (right.ToString() ?? string.Empty);
        }

        if (left is DateTimeOffset dateTimeOffset && TypeConversion.TryConvert(right, typeof(TimeSpan), out var offsetSpan))
        {
            return dateTimeOffset.Add((TimeSpan)offsetSpan!);
        }

        if (left is DateTime dateTime && TypeConversion.TryConvert(right, typeof(TimeSpan), out var dateSpan))
        {
            return dateTime.Add((TimeSpan)dateSpan!);
        }

        if (left is TimeSpan leftSpan && TypeConversion.TryConvert(right, typeof(TimeSpan), out var rightSpan))
        {
            return leftSpan.Add((TimeSpan)rightSpan!);
        }

        if (left is StorageSize leftSize && TypeConversion.TryConvert(right, typeof(StorageSize), out var rightSize))
        {
            return StorageSize.FromBytes(leftSize.Bytes + ((StorageSize)rightSize!).Bytes);
        }

        return EvaluateNumeric(left, right, (lhs, rhs) => lhs + rhs, (lhs, rhs) => lhs + rhs, (lhs, rhs) => lhs + rhs);
    }

    private static object? Subtract(object? left, object? right)
    {
        if (left is null || right is null)
        {
            throw new InvalidOperationException("The '-' operator requires non-null operands.");
        }

        if (left is DateTimeOffset leftOffset)
        {
            if (TypeConversion.TryConvert(right, typeof(TimeSpan), out var offsetSpan))
            {
                return leftOffset.Subtract((TimeSpan)offsetSpan!);
            }

            if (TypeConversion.TryConvert(right, typeof(DateTimeOffset), out var otherOffset))
            {
                return leftOffset.Subtract((DateTimeOffset)otherOffset!);
            }
        }

        if (left is DateTime leftDateTime)
        {
            if (TypeConversion.TryConvert(right, typeof(TimeSpan), out var dateSpan))
            {
                return leftDateTime.Subtract((TimeSpan)dateSpan!);
            }

            if (TypeConversion.TryConvert(right, typeof(DateTime), out var otherDate))
            {
                return leftDateTime.Subtract((DateTime)otherDate!);
            }
        }

        if (left is TimeSpan leftSpan && TypeConversion.TryConvert(right, typeof(TimeSpan), out var rightSpan))
        {
            return leftSpan.Subtract((TimeSpan)rightSpan!);
        }

        if (left is StorageSize leftSize && TypeConversion.TryConvert(right, typeof(StorageSize), out var rightSize))
        {
            return StorageSize.FromBytes(leftSize.Bytes - ((StorageSize)rightSize!).Bytes);
        }

        return EvaluateNumeric(left, right, (lhs, rhs) => lhs - rhs, (lhs, rhs) => lhs - rhs, (lhs, rhs) => lhs - rhs);
    }

    private static object EvaluateNumeric(
        object left,
        object right,
        Func<long, long, long> integral,
        Func<double, double, double> floating,
        Func<decimal, decimal, decimal> precise)
    {
        if (IsDecimal(left) || IsDecimal(right))
        {
            return precise(ToDecimal(left), ToDecimal(right));
        }

        if (IsFloating(left) || IsFloating(right))
        {
            return floating(ToDouble(left), ToDouble(right));
        }

        if (IsIntegral(left) && IsIntegral(right))
        {
            return integral(ToLong(left), ToLong(right));
        }

        throw new InvalidOperationException($"Operator operands '{left.GetType().FullName}' and '{right.GetType().FullName}' are not compatible.");
    }

    private static object? Multiply(object? left, object? right)
    {
        if (left is null || right is null)
        {
            throw new InvalidOperationException("The '*' operator requires non-null operands.");
        }

        return EvaluateNumeric(left, right, (lhs, rhs) => lhs * rhs, (lhs, rhs) => lhs * rhs, (lhs, rhs) => lhs * rhs);
    }

    private static object? Divide(object? left, object? right)
    {
        if (left is null || right is null)
        {
            throw new InvalidOperationException("The '/' operator requires non-null operands.");
        }

        return EvaluateNumeric(
            left,
            right,
            (lhs, rhs) => rhs == 0 ? throw new InvalidOperationException("Division by zero.") : lhs / rhs,
            (lhs, rhs) => lhs / rhs,
            (lhs, rhs) => rhs == 0 ? throw new InvalidOperationException("Division by zero.") : lhs / rhs);
    }

    private static bool ToBoolean(object? value)
    {
        if (TypeConversion.TryConvert(value, typeof(bool), out var converted) && converted is bool boolean)
        {
            return boolean;
        }

        throw new InvalidOperationException("Logical operators require boolean operands.");
    }

    private static bool IsIntegral(object value)
    {
        var type = value.GetType();
        return type == typeof(byte) ||
               type == typeof(sbyte) ||
               type == typeof(short) ||
               type == typeof(ushort) ||
               type == typeof(int) ||
               type == typeof(uint) ||
               type == typeof(long) ||
               type == typeof(ulong);
    }

    private static bool IsFloating(object value)
    {
        var type = value.GetType();
        return type == typeof(float) || type == typeof(double);
    }

    private static bool IsDecimal(object value) => value.GetType() == typeof(decimal);

    private static long ToLong(object value) => Convert.ToInt64(value, CultureInfo.InvariantCulture);

    private static double ToDouble(object value) => Convert.ToDouble(value, CultureInfo.InvariantCulture);

    private static decimal ToDecimal(object value) => Convert.ToDecimal(value, CultureInfo.InvariantCulture);
}
