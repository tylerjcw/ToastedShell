using System.Collections;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Tosh.Core;

internal static class ShellDataSerializer
{
    public static string GetStableKey(object? value)
    {
        return JsonSerializer.Serialize(Normalize(value), JsonOptions);
    }

    public static object? Normalize(object? value)
    {
        return Normalize(value, new HashSet<object>(ReferenceEqualityComparer.Instance), 0);
    }

    public static IReadOnlyDictionary<string, object?> NormalizeRow(object? value)
    {
        var normalized = Normalize(value);

        return normalized switch
        {
            IReadOnlyDictionary<string, object?> readOnly => readOnly,
            IDictionary<string, object?> dictionary => new Dictionary<string, object?>(dictionary, StringComparer.OrdinalIgnoreCase),
            IDictionary dictionary => dictionary.Cast<DictionaryEntry>().ToDictionary(entry => entry.Key?.ToString() ?? string.Empty, entry => entry.Value, StringComparer.OrdinalIgnoreCase),
            _ => new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["Value"] = normalized,
            },
        };
    }

    private static object? Normalize(object? value, ISet<object> visited, int depth)
    {
        if (value is null)
        {
            return null;
        }

        if (depth > 8)
        {
            return "<max-depth>";
        }

        switch (value)
        {
            case ShellTextLine line:
                return line.Text;
            case ProjectedObject projected:
                return projected.Fields.ToDictionary(field => field.Name, field => Normalize(field.Value, visited, depth + 1), StringComparer.OrdinalIgnoreCase);
            case FileSystemPrincipalInfo principal:
                return principal.DisplayName;
            case StorageSize size:
                return size.Bytes;
            case FileSystemEntry entry:
                return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Name"] = entry.Name,
                    ["FullName"] = entry.FullName,
                    ["Type"] = entry.Type.ToString(),
                    ["Extension"] = entry.Extension,
                    ["Size"] = entry.Size?.Bytes,
                    ["Created"] = entry.Created,
                    ["Accessed"] = entry.Accessed,
                    ["Modified"] = entry.Modified,
                    ["Mode"] = entry.Mode?.ToString(),
                    ["Owner"] = entry.Owner?.DisplayName,
                    ["Group"] = entry.Group?.DisplayName,
                    ["Target"] = entry.Target,
                    ["Inode"] = entry.Inode,
                    ["NumLinks"] = entry.NumLinks,
                };
            case IPAddress address:
                return address.ToString();
            case Type type:
                return type.FullName ?? type.Name;
        }

        var typeInfo = value.GetType();
        var effectiveType = Nullable.GetUnderlyingType(typeInfo) ?? typeInfo;

        if (effectiveType.IsEnum ||
            value is string ||
            value is char ||
            value is bool ||
            value is byte ||
            value is short ||
            value is int ||
            value is long ||
            value is float ||
            value is double ||
            value is decimal ||
            value is Guid ||
            value is DateTime ||
            value is DateTimeOffset ||
            value is TimeSpan ||
            value is Uri)
        {
            return value;
        }

        if (value is IDictionary<string, object?> dictionary)
        {
            return dictionary.ToDictionary(entry => entry.Key, entry => Normalize(entry.Value, visited, depth + 1), StringComparer.OrdinalIgnoreCase);
        }

        if (value is IDictionary nonGenericDictionary)
        {
            return nonGenericDictionary.Cast<DictionaryEntry>()
                .ToDictionary(entry => entry.Key?.ToString() ?? string.Empty, entry => Normalize(entry.Value, visited, depth + 1), StringComparer.OrdinalIgnoreCase);
        }

        if (value is IEnumerable enumerable && value is not string)
        {
            return enumerable.Cast<object?>().Select(item => Normalize(item, visited, depth + 1)).ToArray();
        }

        if (!typeInfo.IsValueType)
        {
            if (!visited.Add(value))
            {
                return "<cycle>";
            }
        }

        var properties = typeInfo
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(property => property.CanRead && property.GetIndexParameters().Length == 0);

        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        foreach (var property in properties)
        {
            if (property.Name is "Entry" or "PreferLongDisplay")
            {
                continue;
            }

            object? propertyValue;

            try
            {
                propertyValue = property.GetValue(value);
            }
            catch
            {
                continue;
            }

            result[property.Name] = Normalize(propertyValue, visited, depth + 1);
        }

        return result;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
    };

    private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
    {
        public static readonly ReferenceEqualityComparer Instance = new();

        public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);

        public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
    }
}
