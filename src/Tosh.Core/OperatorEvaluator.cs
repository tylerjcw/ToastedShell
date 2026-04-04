using System.Collections;
using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;

namespace Tosh.Core;

public static class OperatorEvaluator
{
    public static object? EvaluateUnary(string @operator, object? operand)
    {
        return @operator switch
        {
            "not" => !ToBoolean(operand),
            _ => throw new InvalidOperationException($"Unsupported unary operator '{@operator}'."),
        };
    }

    public static object? EvaluateBinary(object? left, string @operator, object? right)
    {
        return @operator switch
        {
            "+" => Add(left, right),
            "-" => Subtract(left, right),
            "*" => Multiply(left, right),
            "/" => Divide(left, right),
            "%" => Modulo(left, right),
            "==" => AreEqual(left, right),
            "!=" => !AreEqual(left, right),
            "=~" => RegexMatch(left, right),
            "!~" => !RegexMatch(left, right),
            "in" => IsIn(left, right),
            "not-in" => !IsIn(left, right),
            ">" => EvaluateOrderedComparison(left, right, nullable: true, comparison => comparison > 0),
            ">=" => EvaluateOrderedComparison(left, right, nullable: true, comparison => comparison >= 0),
            "<" => EvaluateOrderedComparison(left, right, nullable: true, comparison => comparison < 0),
            "<=" => EvaluateOrderedComparison(left, right, nullable: true, comparison => comparison <= 0),
            "contains" => Contains(left, right),
            "starts-with" => StartsWith(left, right),
            "ends-with" => EndsWith(left, right),
            "is" => IsType(left, right),
            "is-not" => !IsType(left, right),
            "and" => ToBoolean(left) && ToBoolean(right),
            "or" => ToBoolean(left) || ToBoolean(right),
            "=" => throw new InvalidOperationException("Assignment operations require a variable."),
            _ => throw new InvalidOperationException($"Unsupported operator '{@operator}'."),
        };
    }

    public static bool Matches(object? actual, string @operator, object? expected, bool nullable)
    {
        return @operator switch
        {
            "==" => AreEqual(actual, expected),
            "!=" => !AreEqual(actual, expected),
            "=~" => RegexMatch(actual, expected),
            "!~" => !RegexMatch(actual, expected),
            "in" => IsIn(actual, expected),
            "not-in" => !IsIn(actual, expected),
            "contains" => Contains(actual, expected),
            "starts-with" => StartsWith(actual, expected),
            "ends-with" => EndsWith(actual, expected),
            ">" => EvaluateOrderedComparison(actual, expected, nullable, comparison => comparison > 0),
            ">=" => EvaluateOrderedComparison(actual, expected, nullable, comparison => comparison >= 0),
            "<" => EvaluateOrderedComparison(actual, expected, nullable, comparison => comparison < 0),
            "<=" => EvaluateOrderedComparison(actual, expected, nullable, comparison => comparison <= 0),
            "is" => IsType(actual, expected),
            "is-not" => !IsType(actual, expected),
            _ => throw new InvalidOperationException($"Unsupported operator '{@operator}'. Supported operators: ==, !=, =~, !~, in, not-in, >, >=, <, <=, contains, starts-with, ends-with, is, is-not."),
        };
    }

    public static bool AreEqual(object? actual, object? expected)
    {
        // When both sides are non-string enumerables, compare element-wise.
        if (actual is IEnumerable actualCollection && actual is not string &&
            expected is IEnumerable expectedCollection && expected is not string)
        {
            var actualEnumerator = actualCollection.GetEnumerator();
            var expectedEnumerator = expectedCollection.GetEnumerator();

            try
            {
                while (true)
                {
                    var actualHasNext = actualEnumerator.MoveNext();
                    var expectedHasNext = expectedEnumerator.MoveNext();

                    if (!actualHasNext && !expectedHasNext)
                    {
                        return true;
                    }

                    if (actualHasNext != expectedHasNext)
                    {
                        return false;
                    }

                    if (!AreEqual(actualEnumerator.Current, expectedEnumerator.Current))
                    {
                        return false;
                    }
                }
            }
            finally
            {
                (actualEnumerator as IDisposable)?.Dispose();
                (expectedEnumerator as IDisposable)?.Dispose();
            }
        }

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

        if (expected is string expectedText &&
            string.Equals(actual.ToString(), expectedText, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (actual is string actualText &&
            string.Equals(actualText, expected.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            return true;
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

    private static bool RegexMatch(object? actual, object? expected)
    {
        var input = actual?.ToString() ?? string.Empty;

        if (expected is Regex regex)
        {
            return regex.IsMatch(input);
        }

        var pattern = expected?.ToString() ?? string.Empty;
        return Regex.IsMatch(input, pattern);
    }

    private static bool IsIn(object? value, object? candidates)
    {
        if (candidates is null)
        {
            return false;
        }

        if (candidates is IDictionary dictionary)
        {
            foreach (DictionaryEntry entry in dictionary)
            {
                if (AreEqual(value, entry.Key))
                {
                    return true;
                }
            }

            return false;
        }

        if (candidates is IEnumerable enumerable && candidates is not string)
        {
            foreach (var candidate in enumerable)
            {
                if (AreEqual(value, candidate))
                {
                    return true;
                }
            }

            return false;
        }

        if (candidates is string text)
        {
            return text.Contains(value?.ToString() ?? string.Empty, StringComparison.Ordinal);
        }

        return AreEqual(value, candidates);
    }

    private static bool StartsWith(object? actual, object? expected)
    {
        return actual?.ToString()?.StartsWith(expected?.ToString() ?? string.Empty, StringComparison.Ordinal) == true;
    }

    private static bool EndsWith(object? actual, object? expected)
    {
        return actual?.ToString()?.EndsWith(expected?.ToString() ?? string.Empty, StringComparison.Ordinal) == true;
    }

    private static bool IsType(object? value, object? typeSpecifier)
    {
        if (value is null)
        {
            return false;
        }

        if (typeSpecifier is Type type)
        {
            return type.IsInstanceOfType(value);
        }

        var typeName = typeSpecifier?.ToString();
        if (string.IsNullOrEmpty(typeName))
        {
            return false;
        }

        var actualType = value.GetType();

        // Check simple name match (e.g. "String", "Int32", "FileSystemEntry")
        if (string.Equals(actualType.Name, typeName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(actualType.FullName, typeName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Resolve via the built-in type alias table (int, string, uint, etc.)
        if (DotNetTypeResolver.BuiltInAliases.TryGetValue(typeName, out var aliasedType))
        {
            return aliasedType.IsInstanceOfType(value);
        }

        // Additional shorthand aliases for convenience (CLR names, common alternatives)
        var resolved = typeName.ToLowerInvariant() switch
        {
            "str" => typeof(string),
            "boolean" => typeof(bool),
            "single" or "float32" => typeof(float),
            "float64" => typeof(double),
            "int8" => typeof(sbyte),
            "uint8" => typeof(byte),
            "int16" => typeof(short),
            "uint16" => typeof(ushort),
            "int32" => typeof(int),
            "uint32" => typeof(uint),
            "int64" => typeof(long),
            "uint64" => typeof(ulong),
            "datetimeoffset" => typeof(DateTimeOffset),
            _ => Type.GetType(typeName, throwOnError: false),
        };

        return resolved is not null && resolved.IsInstanceOfType(value);
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

        if (left is DateTimeOffset offsetInstant && TypeConversion.TryConvert(right, typeof(TemporalAmount), out var offsetAmount))
        {
            return ((TemporalAmount)offsetAmount!).AddTo(offsetInstant);
        }

        if (left is DateTime dateTime && TypeConversion.TryConvert(right, typeof(TimeSpan), out var dateSpan))
        {
            return dateTime.Add((TimeSpan)dateSpan!);
        }

        if (left is DateTime dateInstant && TypeConversion.TryConvert(right, typeof(TemporalAmount), out var dateAmount))
        {
            return ((TemporalAmount)dateAmount!).AddTo(dateInstant);
        }

        if (left is TimeSpan leftSpan && TypeConversion.TryConvert(right, typeof(TimeSpan), out var rightSpan))
        {
            return leftSpan.Add((TimeSpan)rightSpan!);
        }

        if (left is TimeSpan leftTimeSpan && TypeConversion.TryConvert(right, typeof(TemporalAmount), out var rightTemporalAmount))
        {
            return TemporalAmount.FromTimeSpan(leftTimeSpan).Add((TemporalAmount)rightTemporalAmount!);
        }

        if (left is TemporalAmount leftAmount && TypeConversion.TryConvert(right, typeof(TemporalAmount), out var otherAmount))
        {
            return leftAmount.Add((TemporalAmount)otherAmount!);
        }

        if (left is StorageSize leftSize && TypeConversion.TryConvert(right, typeof(StorageSize), out var rightSize))
        {
            return StorageSize.FromBytes(leftSize.Bytes + ((StorageSize)rightSize!).Bytes);
        }

        if (left is IEnumerable leftEnumerable and not string && right is IEnumerable rightEnumerable and not string)
        {
            var result = new List<object?>();

            foreach (var item in leftEnumerable)
            {
                result.Add(item);
            }

            foreach (var item in rightEnumerable)
            {
                result.Add(item);
            }

            return result.ToArray();
        }

        if (right is string rightText)
        {
            return (left.ToString() ?? string.Empty) + rightText;
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

            if (TypeConversion.TryConvert(right, typeof(TemporalAmount), out var offsetAmount))
            {
                return ((TemporalAmount)offsetAmount!).SubtractFrom(leftOffset);
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

            if (TypeConversion.TryConvert(right, typeof(TemporalAmount), out var dateAmount))
            {
                return ((TemporalAmount)dateAmount!).SubtractFrom(leftDateTime);
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

        if (left is TimeSpan spanLeft && TypeConversion.TryConvert(right, typeof(TemporalAmount), out var temporalRight))
        {
            return TemporalAmount.FromTimeSpan(spanLeft).Subtract((TemporalAmount)temporalRight!);
        }

        if (left is TemporalAmount temporalLeft && TypeConversion.TryConvert(right, typeof(TemporalAmount), out var temporalOther))
        {
            return temporalLeft.Subtract((TemporalAmount)temporalOther!);
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
        Func<BigInteger, BigInteger, BigInteger> integral,
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
            try
            {
                var result = integral(ToBigInteger(left), ToBigInteger(right));
                return NarrowIntegralResult(result, left.GetType(), right.GetType());
            }
            catch (OverflowException)
            {
                throw new InvalidOperationException("Arithmetic overflow.");
            }
        }

        throw new InvalidOperationException($"Operator operands '{left.GetType().FullName}' and '{right.GetType().FullName}' are not compatible.");
    }

    // Return the result in the wider of the two integral types, matching C# promotion rules.
    // Small types (byte, sbyte, short, ushort) promote to int, matching C# integer promotion.
    private static object NarrowIntegralResult(BigInteger result, Type leftType, Type rightType)
    {
        var resultType = WiderIntegralType(leftType, rightType);

        try
        {
            if (resultType == typeof(int)) return checked((int)result);
            if (resultType == typeof(uint)) return checked((uint)result);
            if (resultType == typeof(long)) return checked((long)result);
            if (resultType == typeof(ulong)) return checked((ulong)result);
            if (resultType == typeof(BigInteger)) return result;
        }
        catch (OverflowException)
        {
            return result;
        }

        return result;
    }

    private static Type WiderIntegralType(Type a, Type b)
    {
        // Rank: byte/sbyte/short/ushort → int, uint, long, ulong, BigInteger
        return IntegralRank(a) >= IntegralRank(b) ? CanonicalIntegralType(a) : CanonicalIntegralType(b);
    }

    private static int IntegralRank(Type t)
    {
        if (t == typeof(BigInteger)) return 5;
        if (t == typeof(ulong)) return 4;
        if (t == typeof(long)) return 3;
        if (t == typeof(uint)) return 2;
        // int, byte, sbyte, short, ushort all promote to int
        return 1;
    }

    private static Type CanonicalIntegralType(Type t)
    {
        if (t == typeof(BigInteger)) return typeof(BigInteger);
        if (t == typeof(ulong)) return typeof(ulong);
        if (t == typeof(long)) return typeof(long);
        if (t == typeof(uint)) return typeof(uint);
        return typeof(int);
    }

    private static object? Multiply(object? left, object? right)
    {
        if (left is null || right is null)
        {
            throw new InvalidOperationException("The '*' operator requires non-null operands.");
        }

        return EvaluateNumeric(
            left,
            right,
            (lhs, rhs) => lhs * rhs,
            (lhs, rhs) => lhs * rhs,
            (lhs, rhs) => lhs * rhs);
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
            (lhs, rhs) => rhs == 0.0 ? throw new InvalidOperationException("Division by zero.") : lhs / rhs,
            (lhs, rhs) => rhs == 0 ? throw new InvalidOperationException("Division by zero.") : lhs / rhs);
    }

    private static object? Modulo(object? left, object? right)
    {
        if (left is null || right is null)
        {
            throw new InvalidOperationException("The '%' operator requires non-null operands.");
        }

        return EvaluateNumeric(
            left,
            right,
            (lhs, rhs) => rhs == 0 ? throw new InvalidOperationException("Division by zero.") : lhs % rhs,
            (lhs, rhs) => rhs == 0.0 ? throw new InvalidOperationException("Division by zero.") : lhs % rhs,
            (lhs, rhs) => rhs == 0 ? throw new InvalidOperationException("Division by zero.") : lhs % rhs);
    }

    public static bool ToBoolean(object? value)
    {
        switch (value)
        {
            case null: return false;
            case bool b: return b;
            case int i: return i != 0;
            case long l: return l != 0L;
            case double d: return d != 0.0;
            case decimal m: return m != 0m;
            case float f: return f != 0f;
            case byte b: return b != 0;
            case sbyte s: return s != 0;
            case short s: return s != 0;
            case ushort u: return u != 0;
            case uint u: return u != 0U;
            case ulong u: return u != 0UL;
            case BigInteger integer: return integer != BigInteger.Zero;
            case string s: return s.Length > 0;
            case ICollection c: return c.Count > 0;
            case IEnumerable e:
                var enumerator = e.GetEnumerator();
                try { return enumerator.MoveNext(); }
                finally { (enumerator as IDisposable)?.Dispose(); }
            default: return true;
        }
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
               type == typeof(ulong) ||
               type == typeof(BigInteger);
    }

    private static bool IsFloating(object value)
    {
        var type = value.GetType();
        return type == typeof(float) || type == typeof(double);
    }

    private static bool IsDecimal(object value) => value.GetType() == typeof(decimal);

    private static BigInteger ToBigInteger(object value)
    {
        return value switch
        {
            BigInteger integer => integer,
            byte number => new BigInteger(number),
            sbyte number => new BigInteger(number),
            short number => new BigInteger(number),
            ushort number => new BigInteger(number),
            int number => new BigInteger(number),
            uint number => new BigInteger(number),
            long number => new BigInteger(number),
            ulong number => new BigInteger(number),
            _ => throw new InvalidOperationException($"Value of type '{value.GetType().FullName}' cannot be converted to BigInteger."),
        };
    }

    private static double ToDouble(object value) => value is BigInteger integer
        ? (double)integer
        : Convert.ToDouble(value, CultureInfo.InvariantCulture);

    private static decimal ToDecimal(object value) => value is BigInteger integer
        ? (decimal)integer
        : Convert.ToDecimal(value, CultureInfo.InvariantCulture);
}
