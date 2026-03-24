using System.Reflection;

namespace Tosh.Core;

public sealed class ReflectionObjectAccessor : IObjectAccessor
{
    private static readonly NullabilityInfoContext NullabilityContext = new();

    public object? GetValue(object? target, string memberPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(memberPath);

        if (target is null)
        {
            return null;
        }

        object? current = target;

        foreach (var segment in MemberPath.Parse(memberPath).Segments)
        {
            if (current is null)
            {
                return null;
            }

            current = ResolveSegment(current, segment.Name);
        }

        return current;
    }

    public bool IsNullablePath(Type targetType, string memberPath)
    {
        ArgumentNullException.ThrowIfNull(targetType);
        ArgumentException.ThrowIfNullOrWhiteSpace(memberPath);

        if (targetType == typeof(ProjectedObject) ||
            typeof(IReadOnlyDictionary<string, object?>).IsAssignableFrom(targetType) ||
            typeof(IDictionary<string, object?>).IsAssignableFrom(targetType))
        {
            return true;
        }

        var currentType = targetType;
        var pathIsNullable = false;

        foreach (var segment in MemberPath.Parse(memberPath).Segments)
        {
            if (currentType == typeof(ShellTextLine))
            {
                if (string.Equals(segment.Name, nameof(ShellTextLine.Text), StringComparison.OrdinalIgnoreCase))
                {
                    currentType = typeof(string);
                    continue;
                }

                currentType = typeof(string);
            }

            if (ObjectMemberAdapter.TryGetMember(currentType, segment.Name, out var adaptedMember))
            {
                pathIsNullable |= adaptedMember.IsNullable;
                currentType = adaptedMember.ValueType;
                continue;
            }

            var member = ResolveMember(currentType, segment.Name);
            pathIsNullable |= IsNullable(member);
            currentType = GetMemberType(member);
        }

        return pathIsNullable;
    }

    private static object? ResolveSegment(object target, string segment)
    {
        if (target is ShellTextLine textLine)
        {
            if (string.Equals(segment, nameof(ShellTextLine.Text), StringComparison.OrdinalIgnoreCase))
            {
                return textLine.Text;
            }

            target = textLine.Text;
        }

        if (target is ProjectedObject projected && projected.TryGetValue(segment, out var projectedValue))
        {
            return projectedValue;
        }

        if (target is IReadOnlyDictionary<string, object?> readOnlyDictionary &&
            TryGetDictionaryValue(readOnlyDictionary, segment, out var readOnlyDictionaryValue))
        {
            return readOnlyDictionaryValue;
        }

        if (target is IDictionary<string, object?> dictionary &&
            TryGetDictionaryValue(dictionary, segment, out var dictionaryValue))
        {
            return dictionaryValue;
        }

        if (ObjectMemberAdapter.TryGetValue(target, segment, out var adaptedValue))
        {
            return adaptedValue;
        }

        var targetType = target.GetType();
        var member = ResolveMember(targetType, segment);

        return member switch
        {
            PropertyInfo property => property.GetValue(target),
            FieldInfo field => field.GetValue(target),
            _ => throw new InvalidOperationException($"Member '{segment}' was not found on type '{targetType.FullName}'."),
        };
    }

    private static MemberInfo ResolveMember(Type targetType, string segment)
    {
        var flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase;

        var property = targetType.GetProperty(segment, flags);

        if (property is not null && property.GetIndexParameters().Length == 0)
        {
            return property;
        }

        var field = targetType.GetField(segment, flags);

        if (field is not null)
        {
            return field;
        }

        throw new InvalidOperationException($"Member '{segment}' was not found on type '{targetType.FullName}'.");
    }

    private static bool TryGetDictionaryValue(
        IEnumerable<KeyValuePair<string, object?>> dictionary,
        string segment,
        out object? value)
    {
        foreach (var entry in dictionary)
        {
            if (string.Equals(entry.Key, segment, StringComparison.OrdinalIgnoreCase))
            {
                value = entry.Value;
                return true;
            }
        }

        value = null;
        return false;
    }

    private static Type GetMemberType(MemberInfo member)
    {
        return member switch
        {
            PropertyInfo property => property.PropertyType,
            FieldInfo field => field.FieldType,
            _ => typeof(object),
        };
    }

    private static bool IsNullable(MemberInfo member)
    {
        var memberType = GetMemberType(member);

        if (Nullable.GetUnderlyingType(memberType) is not null)
        {
            return true;
        }

        if (memberType.IsValueType)
        {
            return false;
        }

        return member switch
        {
            PropertyInfo property => NullabilityContext.Create(property).ReadState == NullabilityState.Nullable,
            FieldInfo field => NullabilityContext.Create(field).ReadState == NullabilityState.Nullable,
            _ => false,
        };
    }
}
