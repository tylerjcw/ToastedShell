using System.Collections;
using System.Reflection;

namespace Tosh.Runtime;

public static class ShellIndexingUtilities
{
    /// <summary>
    /// Index lookup up to the point where record access would be tried: ranges and every
    /// integer-indexed shape.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Extracted for <c>TS-P1-24</c>, the last of the parallel sync/async internals. The
    /// asynchronous twin was a full copy of this method differing by one branch — an
    /// <see cref="IShellRecordObject"/> lookup that must be awaited — so the convergence is the
    /// shape <c>ReflectionObjectAccessor</c> uses: a shared core with each surface supplying only
    /// its own record step.
    /// </para>
    /// <para>
    /// A plain async prefix would not have worked. The record lookup sits *after* the integer
    /// branches, and <see cref="TryGetIntegerIndex"/> accepts a numeric string, so hoisting the
    /// record access to the front would change what <c>$rec["3"]</c> means — element three rather
    /// than the field named "3". Splitting before/after preserves the order exactly.
    /// </para>
    /// </remarks>
    /// <returns><see langword="true"/> when the value was resolved before the record stage.</returns>
    private static bool TryGetIndexedValueBeforeRecords(
        object target,
        object? index,
        IndexLookupKind lookupKind,
        out object? value)
    {
        if (lookupKind != IndexLookupKind.ByValue && index is ToshRange range)
        {
            value = GetSlice(target, range);
            return true;
        }

        if (lookupKind != IndexLookupKind.ByValue && TryGetIntegerIndex(index, out var numericIndex))
        {
            // Falls through rather than throwing when no integer-indexable shape matches: a
            // numeric *string* reaches here — TryGetIntegerIndex accepts "3" — and must still be
            // able to mean a record field or dictionary key named "3". An earlier version of this
            // extraction ended the integer branch with a throw, which turned `$d["3"]` on a
            // dictionary from a successful key lookup into an error.
            if (TryGetIntegerIndexedValue(target, numericIndex, out value))
            {
                return true;
            }
        }

        value = null;
        return false;
    }

    /// <summary>
    /// Resolves a non-negative integer index against a string, array, list or sequence.
    /// </summary>
    /// <returns>
    /// <see langword="false"/> when <paramref name="target"/> is none of those shapes, so the
    /// caller can carry on to record and dictionary lookup. An out-of-range index on a shape that
    /// *is* integer-indexable still throws, because that is a real error rather than a miss.
    /// </returns>
    private static bool TryGetIntegerIndexedValue(object target, int numericIndex, out object? value)
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

            value = text[numericIndex];
            return true;
        }

        if (target is Array array)
        {
            if (numericIndex >= array.Length)
            {
                throw new InvalidOperationException($"Index {numericIndex} is out of range for array length {array.Length}.");
            }

            value = array.GetValue(numericIndex);
            return true;
        }

        if (target is IList list)
        {
            if (numericIndex >= list.Count)
            {
                throw new InvalidOperationException($"Index {numericIndex} is out of range for list length {list.Count}.");
            }

            value = list[numericIndex];
            return true;
        }

        if (TryGetEnumerableValue(target, numericIndex, out var enumeratedValue))
        {
            value = enumeratedValue;
            return true;
        }

        value = null;
        return false;
    }

    /// <summary>
    /// Index lookup once record access has been tried and missed: by-value field lookup,
    /// dictionaries, and CLR indexer properties.
    /// </summary>
    private static object? GetIndexedValueAfterRecords(object target, object? index, IndexLookupKind lookupKind)
    {
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

    public static object? GetIndexedValue(object? target, object? index, IndexLookupKind lookupKind = IndexLookupKind.Default)
    {
        if (target is null)
        {
            throw new InvalidOperationException("Cannot index into null.");
        }

        if (TryGetIndexedValueBeforeRecords(target, index, lookupKind, out var early))
        {
            return early;
        }

        if (lookupKind != IndexLookupKind.ByValue &&
            index is string keyText &&
            ShellRecordUtilities.TryGetValue(target, keyText, out var recordValue))
        {
            return recordValue;
        }

        return GetIndexedValueAfterRecords(target, index, lookupKind);
    }

    public static async ValueTask<object?> GetIndexedValueAsync(
        object? target,
        object? index,
        IndexLookupKind lookupKind,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (target is null)
        {
            throw new InvalidOperationException("Cannot index into null.");
        }

        if (TryGetIndexedValueBeforeRecords(target, index, lookupKind, out var early))
        {
            return early;
        }

        // The one genuinely asynchronous step, and the only reason this method exists separately.
        if (lookupKind != IndexLookupKind.ByValue && index is string keyText)
        {
            if (target is IShellRecordObject shellRecord)
            {
                var lookup = await shellRecord.TryGetMemberAsync(
                    keyText,
                    includeHidden: false,
                    cancellationToken);

                if (lookup.Found)
                {
                    return lookup.Value;
                }

                // ShellRecordUtilities would dispatch to the record a second time. Preserve any
                // separate generic-dictionary behavior without re-entering the synchronous
                // record API.
                if (target is not IDictionary &&
                    TryGetGenericDictionaryValue(target, keyText, out var genericDictionaryValue))
                {
                    return genericDictionaryValue;
                }
            }
            else if (ShellRecordUtilities.TryGetValue(target, keyText, out var recordValue))
            {
                return recordValue;
            }
        }

        return GetIndexedValueAfterRecords(target, index, lookupKind);
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

    private static bool TryGetGenericDictionaryValue(object target, string key, out object? value)
    {
        if (target is IReadOnlyDictionary<string, object?> readOnlyDictionary)
        {
            foreach (var entry in readOnlyDictionary)
            {
                if (string.Equals(entry.Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    value = entry.Value;
                    return true;
                }
            }
        }

        if (target is IDictionary<string, object?> dictionary)
        {
            foreach (var entry in dictionary)
            {
                if (string.Equals(entry.Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    value = entry.Value;
                    return true;
                }
            }
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

    public static async ValueTask SetIndexedValueAsync(
        object? target,
        object? index,
        object? value,
        IndexLookupKind lookupKind,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (target is null)
        {
            throw new InvalidOperationException("Cannot assign to an index on null.");
        }

        // Do not fall back through ShellRecordUtilities after an async record
        // miss: that would invoke the same record setter synchronously.
        if (index is string keyText)
        {
            if (target is IShellRecordObject record)
            {
                if (await record.TrySetMemberAsync(keyText, value, cancellationToken))
                {
                    return;
                }
            }
            else if (ShellRecordUtilities.TrySetValue(target, keyText, value))
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
