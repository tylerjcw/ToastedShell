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

    public void SetValue(object? target, string memberPath, object? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(memberPath);

        if (target is null)
        {
            throw new InvalidOperationException("Cannot assign a member on null.");
        }

        var segments = MemberPath.Parse(memberPath).Segments;

        if (segments.Count == 0)
        {
            throw new InvalidOperationException("A member path is required for assignment.");
        }

        object? current = target;

        for (var index = 0; index < segments.Count - 1; index++)
        {
            if (current is null)
            {
                throw new InvalidOperationException($"Cannot resolve '{segments[index].Name}' on null.");
            }

            current = ResolveOrMaterializeSegment(current, segments[index].Name);
        }

        if (current is null)
        {
            throw new InvalidOperationException("Cannot assign a member on null.");
        }

        AssignSegment(current, segments[^1].Name, value);
    }

    public bool IsNullablePath(Type targetType, string memberPath)
    {
        ArgumentNullException.ThrowIfNull(targetType);
        ArgumentException.ThrowIfNullOrWhiteSpace(memberPath);

        if (typeof(IReadOnlyDictionary<string, object?>).IsAssignableFrom(targetType) ||
            typeof(IDictionary<string, object?>).IsAssignableFrom(targetType) ||
            typeof(System.Collections.IDictionary).IsAssignableFrom(targetType))
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

        if (target is IShellStaticType shellStaticType &&
            shellStaticType.TryGetStaticMember(segment, out var shellStaticValue))
        {
            return shellStaticValue;
        }

        if (target is Type staticType)
        {
            var flags = BindingFlags.Public | BindingFlags.Static | BindingFlags.IgnoreCase;
            var property = staticType.GetProperty(segment, flags);

            if (property is not null && property.GetIndexParameters().Length == 0)
            {
                return property.GetValue(null);
            }

            var field = staticType.GetField(segment, flags);

            if (field is not null)
            {
                return field.GetValue(null);
            }
        }

        if (ShellRecordUtilities.TryGetValue(target, segment, out var recordValue))
        {
            return recordValue;
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

    private static object? ResolveOrMaterializeSegment(object target, string segment)
    {
        if (ShellRecordUtilities.TryGetValue(target, segment, out var existingValue))
        {
            if (existingValue is null)
            {
                IDictionary<string, object?> created = new System.Dynamic.ExpandoObject();
                ShellRecordUtilities.TrySetValue(target, segment, created);
                return created;
            }

            return existingValue;
        }

        if (target is IDictionary<string, object?>)
        {
            IDictionary<string, object?> created = new System.Dynamic.ExpandoObject();
            ShellRecordUtilities.TrySetValue(target, segment, created);
            return created;
        }

        return ResolveSegment(target, segment);
    }

    private static void AssignSegment(object target, string segment, object? value)
    {
        if (target is ShellTextLine)
        {
            throw new InvalidOperationException("Shell text values are read-only.");
        }

        if (target is IShellRecordObject shellRecord)
        {
            if (shellRecord.TrySetMember(segment, value))
            {
                return;
            }
        }

        if (ShellRecordUtilities.TrySetValue(target, segment, value))
        {
            return;
        }

        var targetType = target.GetType();
        var member = ResolveMember(targetType, segment);

        switch (member)
        {
            case PropertyInfo property:
            {
                if (property.SetMethod is null || !property.SetMethod.IsPublic)
                {
                    if (property.GetValue(target) is IShellRecordObject recordTarget &&
                        value is IDictionary<string, object?> dict)
                    {
                        foreach (var entry in dict)
                        {
                            recordTarget.TrySetMember(entry.Key, entry.Value);
                        }

                        return;
                    }

                    throw new InvalidOperationException($"Property '{segment}' on type '{targetType.FullName}' is read-only.");
                }

                property.SetValue(target, ConvertAssignedValue(value, property.PropertyType, segment, targetType));
                return;
            }

            case FieldInfo field:
            {
                if (field.IsInitOnly || field.IsLiteral)
                {
                    throw new InvalidOperationException($"Field '{segment}' on type '{targetType.FullName}' is read-only.");
                }

                field.SetValue(target, ConvertAssignedValue(value, field.FieldType, segment, targetType));
                return;
            }
        }
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

    private static object? ConvertAssignedValue(object? value, Type targetType, string segment, Type ownerType)
    {
        if (value is null)
        {
            if (!targetType.IsValueType || Nullable.GetUnderlyingType(targetType) is not null)
            {
                return null;
            }

            throw new InvalidOperationException($"Cannot assign null to member '{segment}' on type '{ownerType.FullName}'.");
        }

        if (targetType.IsInstanceOfType(value))
        {
            return value;
        }

        if (TypeConversion.TryConvert(value, targetType, out var converted))
        {
            return converted;
        }

        throw new InvalidOperationException(
            $"Cannot assign value of type '{value.GetType().FullName}' to member '{segment}' on type '{ownerType.FullName}'.");
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
