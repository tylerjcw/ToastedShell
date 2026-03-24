using System.Globalization;
using System.Collections;

namespace Tosh.Core;

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

        if (effectiveType == typeof(TimeSpan) && value is string timeSpanText && TemporalParser.TryParseDuration(timeSpanText, out var timeSpan))
        {
            converted = timeSpan;
            return true;
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

        if (effectiveType == typeof(StorageSize))
        {
            if (value is string storageSizeText && StorageSize.TryParse(storageSizeText, out var parsedSize))
            {
                converted = parsedSize;
                return true;
            }

            if (value is IConvertible)
            {
                try
                {
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

        if (value is IConvertible && typeof(IConvertible).IsAssignableFrom(effectiveType))
        {
            try
            {
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
}
