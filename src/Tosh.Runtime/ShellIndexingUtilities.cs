using System.Collections;
using System.Reflection;

namespace Tosh.Runtime;

public static class ShellIndexingUtilities
{
    public static object? GetIndexedValue(object? target, object? index, IndexLookupKind lookupKind = IndexLookupKind.Default)
    {
        if (target is null)
        {
            throw new InvalidOperationException("Cannot index into null.");
        }

        if (lookupKind != IndexLookupKind.ByValue && TryGetIntegerIndex(index, out var numericIndex))
        {
            if (numericIndex < 0)
            {
                throw new InvalidOperationException("Indexes must be zero or greater.");
            }

            if (target is string text)
            {
                if (numericIndex >= text.Length)
                {
                    throw new InvalidOperationException($"Index {numericIndex} is out of range for string length {text.Length}.");
                }

                return text[numericIndex];
            }

            if (target is Array array)
            {
                if (numericIndex >= array.Length)
                {
                    throw new InvalidOperationException($"Index {numericIndex} is out of range for array length {array.Length}.");
                }

                return array.GetValue(numericIndex);
            }

            if (target is IList list)
            {
                if (numericIndex >= list.Count)
                {
                    throw new InvalidOperationException($"Index {numericIndex} is out of range for list length {list.Count}.");
                }

                return list[numericIndex];
            }

            if (TryGetEnumerableValue(target, numericIndex, out var enumeratedValue))
            {
                return enumeratedValue;
            }
        }

        if (lookupKind != IndexLookupKind.ByValue &&
            index is string keyText &&
            ShellRecordUtilities.TryGetValue(target, keyText, out var recordValue))
        {
            return recordValue;
        }

        if (lookupKind == IndexLookupKind.ByValue &&
            TryGetRecordFieldByValue(target, index, out var recordFieldName))
        {
            return recordFieldName;
        }

        if (TryGetDictionaryValue(target, index, lookupKind, out var dictionaryValue))
        {
            return dictionaryValue;
        }

        if (lookupKind == IndexLookupKind.ByValue)
        {
            throw new InvalidOperationException($"Type '{target.GetType().FullName}' does not support value-based lookup.");
        }

        if (TryGetIndexerPropertyValue(target, index, out var indexedPropertyValue))
        {
            return indexedPropertyValue;
        }

        throw new InvalidOperationException(
            $"Type '{target.GetType().FullName}' does not support index access with '{index?.GetType().FullName ?? "null"}'.");
    }

    private static bool TryGetIntegerIndex(object? index, out int numericIndex)
    {
        numericIndex = 0;

        if (index is int i)
        {
            numericIndex = i;
            return true;
        }

        if (index is long l && l is >= int.MinValue and <= int.MaxValue)
        {
            numericIndex = (int)l;
            return true;
        }

        if (index is double d && d == Math.Floor(d) && d is >= int.MinValue and <= int.MaxValue)
        {
            numericIndex = (int)d;
            return true;
        }

        return TypeConversion.TryConvert(index, typeof(int), out var converted) &&
               converted is int convertedIndex &&
               (numericIndex = convertedIndex) == convertedIndex;
    }

    private static bool TryGetEnumerableValue(object target, int numericIndex, out object? value)
    {
        value = null;

        if (target is not IEnumerable enumerable || target is string || target is IDictionary)
        {
            return false;
        }

        var current = 0;
        foreach (var item in enumerable)
        {
            if (current == numericIndex)
            {
                value = item;
                return true;
            }

            current++;
        }

        throw new InvalidOperationException($"Index {numericIndex} is out of range for sequence length {current}.");
    }

    private static bool TryGetDictionaryValue(object target, object? key, IndexLookupKind lookupKind, out object? value)
    {
        if (target is IDictionary dictionary)
        {
            foreach (DictionaryEntry entry in dictionary)
            {
                if (lookupKind == IndexLookupKind.ByValue)
                {
                    if (OperatorEvaluator.AreEqual(entry.Value, key))
                    {
                        value = entry.Key;
                        return true;
                    }
                }
                else if (OperatorEvaluator.AreEqual(entry.Key, key) ||
                         (key is string keyText && string.Equals(entry.Key?.ToString(), keyText, StringComparison.OrdinalIgnoreCase)))
                {
                    value = entry.Value;
                    return true;
                }
            }

            value = null;
            throw new InvalidOperationException(lookupKind == IndexLookupKind.ByValue
                ? $"Value '{key}' was not found."
                : $"Key '{key}' was not found.");
        }

        value = null;
        return false;
    }

    private static bool TryGetRecordFieldByValue(object target, object? expectedValue, out object? fieldName)
    {
        if (ShellRecordUtilities.TryGetFields(target, out var fields))
        {
            foreach (var field in fields)
            {
                if (OperatorEvaluator.AreEqual(field.Value, expectedValue))
                {
                    fieldName = field.Key;
                    return true;
                }
            }

            fieldName = null;
            throw new InvalidOperationException($"Value '{expectedValue}' was not found.");
        }

        fieldName = null;
        return false;
    }

    private static bool TryGetIndexerPropertyValue(object target, object? index, out object? value)
    {
        var properties = target.GetType()
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.GetIndexParameters().Length == 1);

        foreach (var property in properties)
        {
            var parameter = property.GetIndexParameters()[0];
            object? convertedIndex;

            if (index is null)
            {
                if (parameter.ParameterType.IsValueType && Nullable.GetUnderlyingType(parameter.ParameterType) is null)
                {
                    continue;
                }

                convertedIndex = null;
            }
            else if (parameter.ParameterType.IsInstanceOfType(index))
            {
                convertedIndex = index;
            }
            else if (!TypeConversion.TryConvert(index, parameter.ParameterType, out convertedIndex))
            {
                continue;
            }

            value = property.GetValue(target, [convertedIndex]);
            return true;
        }

        value = null;
        return false;
    }
}
