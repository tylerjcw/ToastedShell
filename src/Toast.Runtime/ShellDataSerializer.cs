using System.Collections;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Tosh.Runtime;

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

    /// <summary>
    /// How deep a value may nest before conversion refuses (<c>TS-P1-43</c>).
    /// </summary>
    /// <remarks>
    /// Cycles are handled by <see cref="WithCycleGuard"/>, so this bounds only genuinely
    /// deep <em>acyclic</em> graphs — reflecting over a CLR object tree such as an
    /// <c>XDocument</c>, where the recursion is bounded by nothing else. 64 is far past
    /// any hand-written structure while still stopping well short of the stack.
    ///
    /// The separation is new. Cycle detection previously covered only the reflection
    /// branch, so a cyclic <em>record</em> was never recognised as a cycle: the depth cap
    /// stopped it and the output said <c>"&lt;max-depth&gt;"</c>, which reads as ordinary
    /// truncation. Raising the cap without also fixing that would have turned a silently
    /// wrong answer into a spurious failure.
    /// </remarks>
    private const int MaxDepth = 64;

    private static object? Normalize(object? value, ISet<object> visited, int depth)
    {
        if (value is null)
        {
            return null;
        }

        if (depth > MaxDepth)
        {
            // `TS-P1-43`. This used to substitute the string "<max-depth>" and carry on,
            // so `to json` reported success while quietly replacing real values with a
            // placeholder — the VS Code grammar, which nests nine deep at
            // `repository → rule → captures → "2" → patterns → item`, came out with
            // sixteen rules whose `match` and `name` were both the literal
            // `"<max-depth>"`. Valid JSON, wrong content, no diagnostic.
            //
            // Failing is the honest answer: a serializer that cannot represent the value
            // has not serialized it. Cycles are caught separately by `WithCycleGuard`, so
            // reaching here means the value really is this deep — the old cap of 8 was far
            // below anything a person would consider deep.
            throw ToshDiagnosticException.Create(new ToshDiagnostic(
                Code: "tosh.runtime.serialization_depth_exceeded",
                Title: $"Value nests deeper than {MaxDepth} levels and cannot be serialized.",
                Help: "project the parts you need, or flatten the value before converting it."));
        }

        switch (value)
        {
            case ShellTextLine line:
                return line.Text;
            case FileSystemPrincipalInfo principal:
                return principal.DisplayName;
            case StorageSize size:
                return size.Bytes;
            case TemporalAmount amount:
                return amount.ToString();
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

        if (ShellRecordUtilities.TryGetFields(value, out var recordFields))
        {
            // `TS-P1-43`. Records were recursed without touching `visited`, so a cyclic
            // record was never detected as a cycle — the depth cap stopped the recursion
            // and the result was a `"<max-depth>"` placeholder that looked like ordinary
            // truncation. Cycle and "too deep" are different conditions and now report
            // differently: `"<cycle>"` here, a diagnostic above.
            return WithCycleGuard(value, visited, () => recordFields.ToDictionary(
                field => field.Key,
                field => Normalize(field.Value, visited, depth + 1),
                StringComparer.OrdinalIgnoreCase));
        }

        // `TOAST-0088`. A shell-declared enum is a `ToshEnumValue` object, so `IsEnum` below is
        // false for it and it fell through to the reflection tail — which emitted `Definition`,
        // `Name`, `UnderlyingValue`, `ShellTypeDescriptor` and `EnumTypeName`, with the type
        // descriptor twice. A CLR enum serialised to one scalar; a shell enum to twenty-three
        // lines of JSON, five CSV columns, and the same again in TOML and XML, because every
        // format reaches this one method.
        //
        // The member name rather than the number: it is what `ToString` already gives, it is
        // what survives a round trip legibly, and a config file that says "Librarian" beats one
        // that says 8. CLR enums still serialise as numbers — that is .NET's default and is a
        // separate decision from this one.
        if (value is IShellEnumValue shellEnum)
        {
            return shellEnum.Name;
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
            value is TemporalAmount ||
            value is TimeSpan ||
            value is Uri)
        {
            return value;
        }

        if (value is IDictionary<string, object?> dictionary)
        {
            return WithCycleGuard(value, visited, () => dictionary.ToDictionary(entry => entry.Key, entry => Normalize(entry.Value, visited, depth + 1), StringComparer.OrdinalIgnoreCase));
        }

        if (value is IDictionary nonGenericDictionary)
        {
            return WithCycleGuard(value, visited, () => nonGenericDictionary.Cast<DictionaryEntry>()
                .ToDictionary(entry => entry.Key?.ToString() ?? string.Empty, entry => Normalize(entry.Value, visited, depth + 1), StringComparer.OrdinalIgnoreCase));
        }

        if (value is IEnumerable enumerable && value is not string)
        {
            return WithCycleGuard(value, visited, () => enumerable.Cast<object?>().Select(item => Normalize(item, visited, depth + 1)).ToArray());
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

    /// <summary>
    /// Runs <paramref name="build"/> with <paramref name="value"/> marked as being
    /// walked, so a structure that contains itself reports <c>"&lt;cycle&gt;"</c> instead
    /// of recursing (<c>TS-P1-43</c>).
    /// </summary>
    /// <remarks>
    /// The mark is removed afterwards. Without that, a value legitimately reachable
    /// twice by different paths — the same record in two fields, a shared list — would
    /// be reported as a cycle the second time, which is a DAG rather than a loop.
    /// </remarks>
    private static object? WithCycleGuard(object value, ISet<object> visited, Func<object?> build)
    {
        if (value.GetType().IsValueType)
        {
            return build();
        }

        if (!visited.Add(value))
        {
            return "<cycle>";
        }

        try
        {
            return build();
        }
        finally
        {
            visited.Remove(value);
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = ToshJson.Compact;

    private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
    {
        public static readonly ReferenceEqualityComparer Instance = new();

        public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);

        public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
    }
}
