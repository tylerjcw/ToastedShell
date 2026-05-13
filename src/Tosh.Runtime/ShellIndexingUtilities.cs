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

        if (lookupKind != IndexLookupKind.ByValue && index is ToshRange range)
        {
            return GetSlice(target, range);
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

    private static object GetSlice(object target, ToshRange range)
    {
        if (range.IsInfinite)
        {
            throw new InvalidOperationException("Cannot slice with an open-ended range.");
        }

        var indices = range.Enumerate().ToList();
        var result = new List<object?>(indices.Count);

        // String slice: return a string instead of a list of chars.
        if (target is string text)
        {
            var sb = new System.Text.StringBuilder(indices.Count);
            foreach (var idx in indices)
            {
                if (idx < 0 || idx >= text.Length)
                {
                    throw new InvalidOperationException($"Index {idx} is out of range for string length {text.Length}.");
                }
                sb.Append(text[idx]);
            }
            return sb.ToString();
        }

        if (target is Array array)
        {
            foreach (var idx in indices)
            {
                if (idx < 0 || idx >= array.Length)
                {
                    throw new InvalidOperationException($"Index {idx} is out of range for array length {array.Length}.");
                }
                result.Add(array.GetValue(idx));
            }
            return result;
        }

        if (target is IList list)
        {
            foreach (var idx in indices)
            {
                if (idx < 0 || idx >= list.Count)
                {
                    throw new InvalidOperationException($"Index {idx} is out of range for list length {list.Count}.");
                }
                result.Add(list[idx]);
            }
            return result;
        }

        if (target is IEnumerable enumerable && target is not IDictionary)
        {
            // Materialize once so we can index repeatedly.
            var materialized = new List<object?>();
            foreach (var item in enumerable) materialized.Add(item);

            foreach (var idx in indices)
            {
                if (idx < 0 || idx >= materialized.Count)
                {
                    throw new InvalidOperationException($"Index {idx} is out of range for sequence length {materialized.Count}.");
                }
                result.Add(materialized[idx]);
            }
            return result;
        }

        throw new InvalidOperationException(
            $"Type '{target.GetType().FullName}' does not support range slicing.");
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

    /// <summary>
    /// Assigns <paramref name="value"/> to <paramref name="target"/> at
    /// <paramref name="index"/>. Mirrors <see cref="GetIndexedValue"/>:
    /// supports <see cref="IShellRecordObject"/>, <see cref="IList"/>,
    /// <see cref="Array"/>, <see cref="IDictionary"/>, and CLR indexer
    /// properties.
    /// </summary>
    public static void SetIndexedValue(object? target, object? index, object? value, IndexLookupKind lookupKind = IndexLookupKind.Default)
    {
        if (target is null)
        {
            throw new InvalidOperationException("Cannot assign to an index on null.");
        }

        // String-keyed write into a record-object first — covers $tosh.Config.Schemas["k"] = v.
        if (index is string keyText)
        {
            if (target is IShellRecordObject record && record.TrySetMember(keyText, value))
            {
                return;
            }

            if (ShellRecordUtilities.TrySetValue(target, keyText, value))
            {
                return;
            }
        }

        if (TryGetIntegerIndex(index, out var numericIndex))
        {
            if (target is Array array)
            {
                if (numericIndex < 0 || numericIndex >= array.Length)
                {
                    throw new InvalidOperationException($"Index {numericIndex} is out of range for array length {array.Length}.");
                }

                array.SetValue(value, numericIndex);
                return;
            }

            if (target is IList list)
            {
                if (numericIndex < 0 || numericIndex >= list.Count)
                {
                    throw new InvalidOperationException($"Index {numericIndex} is out of range for list length {list.Count}.");
                }

                list[numericIndex] = value;
                return;
            }
        }

        if (target is IDictionary dictionary)
        {
            object? dictKey = index;

            if (index is string textKey)
            {
                foreach (DictionaryEntry entry in dictionary)
                {
                    if (string.Equals(entry.Key?.ToString(), textKey, StringComparison.OrdinalIgnoreCase))
                    {
                        dictKey = entry.Key;
                        break;
                    }
                }
            }

            dictionary[dictKey!] = value;
            return;
        }

        if (TrySetIndexerProperty(target, index, value))
        {
            return;
        }

        throw new InvalidOperationException(
            $"Type '{target.GetType().FullName}' does not support index assignment with '{index?.GetType().FullName ?? "null"}'.");
    }

    private static bool TrySetIndexerProperty(object target, object? index, object? value)
    {
        var properties = target.GetType()
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.CanWrite && property.GetIndexParameters().Length == 1);

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

            object? convertedValue = value;
            if (value is not null
                && !property.PropertyType.IsInstanceOfType(value)
                && !TypeConversion.TryConvert(value, property.PropertyType, out convertedValue))
            {
                continue;
            }

            property.SetValue(target, convertedValue, [convertedIndex]);
            return true;
        }

        return false;
    }
}
