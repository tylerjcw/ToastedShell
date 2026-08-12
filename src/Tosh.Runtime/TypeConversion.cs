using System.Globalization;
using System.Collections;
using System.Net;
using System.Numerics;
using Tosh.Runtime.Units;

namespace Tosh.Runtime;

public static class TypeConversion
{
    public static bool TryConvert(object? value, Type targetType, out object? converted)
    {
        ArgumentNullException.ThrowIfNull(targetType);

        var effectiveType = Nullable.GetUnderlyingType(targetType) ?? targetType;

        if (value is null)
        {
            if (!effectiveType.IsValueType || Nullable.GetUnderlyingType(targetType) is not null)
            {
                converted = null;
                return true;
            }

            converted = null;
            return false;
        }

        if (effectiveType.IsInstanceOfType(value))
        {
            converted = value;
            return true;
        }

        // An enum value converts through the number behind it, not through its name. Without
        // this the value fell to the string conversions below and `cast int Fuel.Uranium`
        // reported "Could not cast 'Uranium' to System.Int32" — the name, which is what
        // `ToString` returns and is never what a numeric cast is asking for.
        //
        // Conversion to string is left alone: the name is the right answer there, and the
        // instance check above already returns the value itself when the target is the enum type.
        if (value is IShellEnumValue enumValue &&
            effectiveType != typeof(string) &&
            enumValue.UnderlyingValue is { } underlying)
        {
            return TryConvert(underlying, targetType, out converted);
        }

        if (effectiveType == typeof(Complex))
        {
            if (ComplexShellType.TryConvert(value, out var complex))
            {
                converted = complex;
                return true;
            }

            converted = null;
            return false;
        }

        // Quantity annotations are real conversion boundaries, not merely runtime
        // instance checks. This is what lets an OS argv string such as "5km" bind
        // to a script parameter declared as LengthQuantity.
        if (typeof(Quantity).IsAssignableFrom(effectiveType))
        {
            Quantity? quantity = value switch
            {
                Quantity existing => existing,
                string quantityText when Quantity.TryParse(quantityText, out var parsed) => parsed,
                TimeSpan span => new DurationQuantity(span.TotalSeconds, "s"),
                StorageSize size when IsExactlyRepresentableAsDouble(size.Bytes) =>
                    new DataSizeQuantity(size.Bytes, "B"),
                _ => null,
            };

            if (quantity is not null)
            {
                if (effectiveType == typeof(Quantity) || effectiveType.IsInstanceOfType(quantity))
                {
                    converted = quantity;
                    return true;
                }

                // Named quantity subclasses all expose the same (magnitude, unit)
                // constructor. Recreate the value only when that subtype accepts
                // the parsed dimension; its base constructor performs the check.
                var constructor = effectiveType.GetConstructor([typeof(double), typeof(string)]);
                if (constructor is not null)
                {
                    try
                    {
                        converted = constructor.Invoke([quantity.Magnitude, quantity.UnitSymbol]);
                        return true;
                    }
                    catch
                    {
                        // The requested category has a different dimension.
                    }
                }
            }

            converted = null;
            return false;
        }

        if (effectiveType == typeof(string))
        {
            converted = value.ToString();
            return true;
        }

        if (effectiveType.IsEnum && value is string enumText)
        {
            try
            {
                converted = Enum.Parse(effectiveType, enumText, ignoreCase: true);
                return true;
            }
            catch
            {
                converted = null;
                return false;
            }
        }

        if (effectiveType == typeof(Guid) && value is string guidText && Guid.TryParse(guidText, out var guid))
        {
            converted = guid;
            return true;
        }

        if (effectiveType == typeof(BigInteger))
        {
            try
            {
                if (value is string bigIntegerText &&
                    BigInteger.TryParse(bigIntegerText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedBigInteger))
                {
                    converted = parsedBigInteger;
                    return true;
                }

                if (WouldLoseFractionalPrecision(value, typeof(BigInteger)))
                {
                    converted = null;
                    return false;
                }

                converted = value switch
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
                    decimal number => new BigInteger(number),
                    double number => new BigInteger(number),
                    float number => new BigInteger(number),
                    _ => null,
                };
                return converted is not null;
            }
            catch
            {
                converted = null;
                return false;
            }
        }

        if (value is BigInteger sourceBigInteger)
        {
            try
            {
                if (effectiveType == typeof(string))
                {
                    converted = sourceBigInteger.ToString(CultureInfo.InvariantCulture);
                    return true;
                }

                converted = effectiveType switch
                {
                    var type when type == typeof(byte) => checked((byte)sourceBigInteger),
                    var type when type == typeof(sbyte) => checked((sbyte)sourceBigInteger),
                    var type when type == typeof(short) => checked((short)sourceBigInteger),
                    var type when type == typeof(ushort) => checked((ushort)sourceBigInteger),
                    var type when type == typeof(int) => checked((int)sourceBigInteger),
                    var type when type == typeof(uint) => checked((uint)sourceBigInteger),
                    var type when type == typeof(long) => checked((long)sourceBigInteger),
                    var type when type == typeof(ulong) => checked((ulong)sourceBigInteger),
                    var type when type == typeof(decimal) => (decimal)sourceBigInteger,
                    var type when type == typeof(double) => (double)sourceBigInteger,
                    var type when type == typeof(float) => (float)sourceBigInteger,
                    _ => null,
                };

                return converted is not null;
            }
            catch
            {
                converted = null;
                return false;
            }
        }

        if (effectiveType == typeof(DateOnly))
        {
            if (value is string dateOnlyText)
            {
                if (DateOnly.TryParse(dateOnlyText, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var dateOnly))
                {
                    converted = dateOnly;
                    return true;
                }

                if (TemporalParser.TryParseDateTimeOffset(dateOnlyText, out var parsedDateOnlyOffset))
                {
                    converted = DateOnly.FromDateTime(parsedDateOnlyOffset.DateTime);
                    return true;
                }
            }

            if (value is DateTime dateOnlyDateTime)
            {
                converted = DateOnly.FromDateTime(dateOnlyDateTime);
                return true;
            }

            if (value is DateTimeOffset dateOnlyOffset)
            {
                converted = DateOnly.FromDateTime(dateOnlyOffset.DateTime);
                return true;
            }
        }

        if (effectiveType == typeof(TimeOnly))
        {
            if (value is string timeOnlyText)
            {
                if (TimeOnly.TryParse(timeOnlyText, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var timeOnly))
                {
                    converted = timeOnly;
                    return true;
                }

                if (TemporalParser.TryParseDateTimeOffset(timeOnlyText, out var parsedTimeOnlyOffset))
                {
                    converted = TimeOnly.FromDateTime(parsedTimeOnlyOffset.DateTime);
                    return true;
                }
            }

            if (value is DateTime timeOnlyDateTime)
            {
                converted = TimeOnly.FromDateTime(timeOnlyDateTime);
                return true;
            }

            if (value is DateTimeOffset timeOnlyOffset)
            {
                converted = TimeOnly.FromDateTime(timeOnlyOffset.DateTime);
                return true;
            }
        }

        if (effectiveType == typeof(IPAddress) && value is string addressText && IPAddress.TryParse(addressText, out var address))
        {
            converted = address;
            return true;
        }

        if (effectiveType == typeof(TimeSpan) && value is Quantity durationQuantity &&
            durationQuantity.Dimension == UnitExpression.Of(UnitDimension.Time))
        {
            if (durationQuantity is DurationQuantity duration && duration.TryToTimeSpan(out var bridgedSpan))
            {
                converted = bridgedSpan;
                return true;
            }

            try
            {
                var seconds = durationQuantity.BaseValue;
                if (double.IsFinite(seconds))
                {
                    converted = TimeSpan.FromSeconds(seconds);
                    return true;
                }
            }
            catch (OverflowException)
            {
            }

            converted = null;
            return false;
        }

        if (effectiveType == typeof(TimeSpan) && value is string timeSpanText && TemporalParser.TryParseDuration(timeSpanText, out var timeSpan))
        {
            converted = timeSpan;
            return true;
        }

        if (effectiveType == typeof(TimeSpan) && value is TemporalAmount temporalAmount && temporalAmount.TryAsTimeSpan(out var temporalSpan))
        {
            converted = temporalSpan;
            return true;
        }

        if (effectiveType == typeof(TimeSpan) && value is byte or short or int or long or float or double or decimal)
        {
            try
            {
                var seconds = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                if (double.IsFinite(seconds))
                {
                    converted = TimeSpan.FromSeconds(seconds);
                    return true;
                }
            }
            catch (Exception exception) when (
                exception is OverflowException or ArgumentException or InvalidCastException or FormatException)
            {
            }

            converted = null;
            return false;
        }

        if (effectiveType == typeof(TemporalAmount))
        {
            if (value is string temporalText && TemporalParser.TryParseTemporalAmount(temporalText, out var parsedTemporalAmount))
            {
                converted = parsedTemporalAmount;
                return true;
            }

            if (value is TimeSpan timeSpanValue)
            {
                converted = TemporalAmount.FromTimeSpan(timeSpanValue);
                return true;
            }

            if (value is Quantity durationValue &&
                durationValue.Dimension == UnitExpression.Of(UnitDimension.Time) &&
                TryConvert(durationValue, typeof(TimeSpan), out var durationSpan) &&
                durationSpan is TimeSpan convertedDuration)
            {
                converted = TemporalAmount.FromTimeSpan(convertedDuration);
                return true;
            }
        }

        if (effectiveType == typeof(DateTimeOffset))
        {
            if (value is string dateTimeOffsetText && TemporalParser.TryParseDateTimeOffset(dateTimeOffsetText, out var dateTimeOffset))
            {
                converted = dateTimeOffset;
                return true;
            }

            if (value is DateTime dateTimeValue)
            {
                converted = dateTimeValue.Kind == DateTimeKind.Unspecified
                    ? new DateTimeOffset(DateTime.SpecifyKind(dateTimeValue, DateTimeKind.Local))
                    : new DateTimeOffset(dateTimeValue);
                return true;
            }
        }

        if (effectiveType == typeof(DateTime))
        {
            if (value is string dateTimeText && TemporalParser.TryParseDateTime(dateTimeText, out var parsedDateTime))
            {
                converted = parsedDateTime;
                return true;
            }

            if (value is DateTimeOffset dateTimeOffsetValue)
            {
                converted = dateTimeOffsetValue.LocalDateTime;
                return true;
            }
        }

        if (effectiveType == typeof(Uri) && value is string uriText && Uri.TryCreate(uriText, UriKind.RelativeOrAbsolute, out var uri))
        {
            converted = uri;
            return true;
        }

        if (effectiveType == typeof(IntPtr))
        {
            try
            {
                if (WouldLoseFractionalPrecision(value, typeof(long)))
                {
                    converted = null;
                    return false;
                }

                converted = value switch
                {
                    NativeBuffer buffer => buffer.Pointer,
                    UIntPtr unsignedPointer => new IntPtr(unchecked((long)unsignedPointer.ToUInt64())),
                    IConvertible => new IntPtr(Convert.ToInt64(value, CultureInfo.InvariantCulture)),
                    _ => null,
                };
                return converted is not null;
            }
            catch
            {
                converted = null;
                return false;
            }
        }

        if (effectiveType == typeof(UIntPtr))
        {
            try
            {
                if (WouldLoseFractionalPrecision(value, typeof(ulong)))
                {
                    converted = null;
                    return false;
                }

                converted = value switch
                {
                    NativeBuffer buffer => new UIntPtr(Convert.ToUInt64(buffer.Pointer.ToInt64(), CultureInfo.InvariantCulture)),
                    IntPtr signedPointer when signedPointer.ToInt64() >= 0 => new UIntPtr(Convert.ToUInt64(signedPointer.ToInt64(), CultureInfo.InvariantCulture)),
                    IConvertible => new UIntPtr(Convert.ToUInt64(value, CultureInfo.InvariantCulture)),
                    _ => null,
                };
                return converted is not null;
            }
            catch
            {
                converted = null;
                return false;
            }
        }

        if (effectiveType.IsEnum)
        {
            var underlyingType = Enum.GetUnderlyingType(effectiveType);

            if (TryConvert(value, underlyingType, out var convertedUnderlying) && convertedUnderlying is not null)
            {
                try
                {
                    converted = Enum.ToObject(effectiveType, convertedUnderlying);
                    return true;
                }
                catch
                {
                }
            }
        }

        if (effectiveType == typeof(StorageSize))
        {
            if (value is DataSizeQuantity dataSize && dataSize.TryToStorageSize(out var bridgedSize))
            {
                converted = bridgedSize;
                return true;
            }

            if (value is Quantity dataQuantity &&
                dataQuantity.Dimension == UnitExpression.Of(UnitDimension.Data))
            {
                var bytes = dataQuantity.To("B").Magnitude;
                if (double.IsFinite(bytes) &&
                    bytes == Math.Truncate(bytes) &&
                    bytes >= -9_223_372_036_854_775_808d &&
                    bytes < 9_223_372_036_854_775_808d)
                {
                    converted = StorageSize.FromBytes((long)bytes);
                    return true;
                }

                converted = null;
                return false;
            }

            if (value is string storageSizeText && StorageSize.TryParse(storageSizeText, out var parsedSize))
            {
                converted = parsedSize;
                return true;
            }

            if (value is IConvertible)
            {
                try
                {
                    if (WouldLoseFractionalPrecision(value, typeof(long)))
                    {
                        converted = null;
                        return false;
                    }

                    converted = StorageSize.FromBytes(Convert.ToInt64(value, CultureInfo.InvariantCulture));
                    return true;
                }
                catch
                {
                    converted = null;
                    return false;
                }
            }
        }

        if (effectiveType.IsArray && value is IEnumerable enumerable && value is not string)
        {
            var elementType = effectiveType.GetElementType()
                              ?? throw new InvalidOperationException($"Array type '{effectiveType.FullName}' is missing an element type.");
            var items = new List<object?>();

            foreach (var item in enumerable)
            {
                if (!TryConvert(item, elementType, out var convertedItem))
                {
                    converted = null;
                    return false;
                }

                items.Add(convertedItem);
            }

            var array = Array.CreateInstance(elementType, items.Count);

            for (var index = 0; index < items.Count; index++)
            {
                array.SetValue(items[index], index);
            }

            converted = array;
            return true;
        }

        if (value is IEnumerable genericEnumerable &&
            value is not string &&
            TryConvertEnumerable(genericEnumerable, effectiveType, out converted))
        {
            return true;
        }

        if (value is IConvertible && typeof(IConvertible).IsAssignableFrom(effectiveType))
        {
            try
            {
                if (WouldLoseFractionalPrecision(value, effectiveType))
                {
                    converted = null;
                    return false;
                }

                converted = Convert.ChangeType(value, effectiveType, CultureInfo.InvariantCulture);
                return true;
            }
            catch
            {
                converted = null;
                return false;
            }
        }

        converted = null;
        return false;
    }

    private static bool IsExactlyRepresentableAsDouble(long value)
    {
        if (value == 0) return true;

        // Binary64 has 53 significant bits. Larger integers remain exact only
        // when enough low bits are zero (for example 2^53 + 2 and long.MinValue).
        // Avoid casting an out-of-range rounded double back to long.
        var magnitude = value < 0
            ? (ulong)(-(value + 1)) + 1UL
            : (ulong)value;
        var significantBits =
            (64 - BitOperations.LeadingZeroCount(magnitude)) -
            BitOperations.TrailingZeroCount(magnitude);
        return significantBits <= 53;
    }

    private static bool TryConvertEnumerable(IEnumerable enumerable, Type targetType, out object? converted)
    {
        var elementType = GetEnumerableElementType(targetType);

        if (elementType is null)
        {
            converted = null;
            return false;
        }

        var listType = typeof(List<>).MakeGenericType(elementType);
        var list = (IList)Activator.CreateInstance(listType)!;

        foreach (var item in enumerable)
        {
            if (!TryConvert(item, elementType, out var convertedItem))
            {
                converted = null;
                return false;
            }

            list.Add(convertedItem);
        }

        if (targetType.IsAssignableFrom(listType))
        {
            converted = list;
            return true;
        }

        var enumerableType = typeof(IEnumerable<>).MakeGenericType(elementType);
        var constructor = targetType.GetConstructor([enumerableType]);

        if (constructor is not null)
        {
            converted = constructor.Invoke([list]);
            return true;
        }

        converted = null;
        return false;
    }

    private static Type? GetEnumerableElementType(Type type)
    {
        if (type.IsGenericType)
        {
            var genericDefinition = type.GetGenericTypeDefinition();

            if (genericDefinition == typeof(IEnumerable<>) ||
                genericDefinition == typeof(IReadOnlyCollection<>) ||
                genericDefinition == typeof(ICollection<>) ||
                genericDefinition == typeof(IReadOnlyList<>) ||
                genericDefinition == typeof(IList<>) ||
                genericDefinition == typeof(List<>))
            {
                return type.GetGenericArguments()[0];
            }
        }

        return type
            .GetInterfaces()
            .FirstOrDefault(candidate => candidate.IsGenericType &&
                                         candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            ?.GetGenericArguments()[0];
    }

    private static bool WouldLoseFractionalPrecision(object value, Type targetType)
    {
        if (!IsIntegralType(targetType))
        {
            return false;
        }

        return value switch
        {
            float single => !float.IsFinite(single) || single != MathF.Truncate(single),
            double number => !double.IsFinite(number) || number != Math.Truncate(number),
            decimal decimalValue => decimalValue != decimal.Truncate(decimalValue),
            _ => false,
        };
    }

    private static bool IsIntegralType(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;

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
}
