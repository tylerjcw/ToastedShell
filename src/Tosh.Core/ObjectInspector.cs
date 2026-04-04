using System.Collections;
using System.Reflection;

namespace Tosh.Core;

public sealed class ObjectInspector
{
    private const int DefaultMemberLimit = 12;
    private const int DefaultItemPreviewLimit = 8;
    private readonly ObjectFormatter _formatter;

    public ObjectInspector(ObjectFormatter formatter)
    {
        _formatter = formatter;
    }

    public ObjectInspection Inspect(object? value, int index, bool includeAllMembers = false)
    {
        if (value is null)
        {
            return new ObjectInspection(
                index,
                TypeName: "null",
                AssemblyName: null,
                BaseTypeName: null,
                Display: "null",
                IsEnumerable: false,
                ItemCount: null,
                Interfaces: Array.Empty<string>(),
                Members: Array.Empty<ObjectInspectionMember>(),
                ItemsPreview: Array.Empty<string>(),
                HasMoreItems: false);
        }

        var runtimeType = value.GetType();
        var previewOptions = new ObjectFormattingOptions(ObjectRenderStyle.Compact, MaxDepth: 1, MaxCollectionItemCount: 4, MaxPropertyCount: 4);
        var memberLimit = includeAllMembers ? int.MaxValue : DefaultMemberLimit;
        var hasRecordFields = ShellRecordUtilities.TryGetFields(value, out var recordFields);
        var shellRecord = value as IShellRecordObject;
        IShellTypeDescriptor? shellDescriptor = value as IShellTypeDescriptor;

        if (shellDescriptor is null && value is IShellTypedObject typed)
        {
            shellDescriptor = typed.ShellTypeDescriptor;
        }

        if (shellDescriptor is null &&
            BuiltInShellTypes.TryDescribeRuntimeValue(value, out var builtInDescriptor))
        {
            shellDescriptor = builtInDescriptor;
        }

        return new ObjectInspection(
            index,
            TypeName: shellDescriptor?.ShellTypeName ?? shellRecord?.ShellTypeName ?? runtimeType.FullName ?? runtimeType.Name,
            AssemblyName: shellDescriptor is null && shellRecord is null ? runtimeType.Assembly.GetName().Name : "ToSh",
            BaseTypeName: shellDescriptor is null && shellRecord is null ? runtimeType.BaseType?.FullName : "System.Object",
            Display: _formatter.Format(value, previewOptions),
            IsEnumerable: !hasRecordFields && value is IEnumerable and not string,
            ItemCount: hasRecordFields ? null : TryGetCount(value),
            Interfaces: shellDescriptor is null && shellRecord is null
                ? runtimeType.GetInterfaces()
                    .Select(type => type.FullName ?? type.Name)
                    .OrderBy(name => name, StringComparer.Ordinal)
                    .Take(12)
                    .ToArray()
                : Array.Empty<string>(),
            Members: runtimeType.IsEnum
                ? GetEnumMembers(value, runtimeType, previewOptions)
                : hasRecordFields
                    ? GetRecordMembers(recordFields, memberLimit, previewOptions)
                    : GetMembers(value, runtimeType, memberLimit, previewOptions),
            ItemsPreview: hasRecordFields ? Array.Empty<string>() : GetItemsPreview(value, previewOptions),
            HasMoreItems: !hasRecordFields && HasMoreItems(value, DefaultItemPreviewLimit));
    }

    private IReadOnlyList<ObjectInspectionMember> GetEnumMembers(object value, Type runtimeType, ObjectFormattingOptions previewOptions)
    {
        var members = new List<ObjectInspectionMember>();
        var underlyingType = Enum.GetUnderlyingType(runtimeType);
        var numericValue = ReflectionMetadataUtilities.GetEnumNumericValue((Enum)value);

        members.Add(new ObjectInspectionMember(
            Name: "NumericValue",
            MemberKind: "enum",
            TypeName: underlyingType.FullName ?? underlyingType.Name,
            Display: _formatter.Format(numericValue, previewOptions)));

        var names = ReflectionMetadataUtilities.GetEnumNames((Enum)value);

        if (names.Count > 0)
        {
            members.Add(new ObjectInspectionMember(
                Name: "Names",
                MemberKind: "enum",
                TypeName: "System.String[]",
                Display: _formatter.Format(names.ToArray(), previewOptions)));
        }

        return members;
    }

    private IReadOnlyList<ObjectInspectionMember> GetMembers(object value, Type runtimeType, int memberLimit, ObjectFormattingOptions previewOptions)
    {
        var members = new List<ObjectInspectionMember>();

        foreach (var property in runtimeType
                     .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                     .Where(property => property.CanRead && property.GetIndexParameters().Length == 0)
                     .OrderBy(property => property.Name, StringComparer.Ordinal))
        {
            if (members.Count >= memberLimit)
            {
                return members;
            }

            var propertyType = property.PropertyType;
            object? propertyValue;

            if (ObjectMemberAdapter.TryGetMember(runtimeType, property.Name, out var adaptedMember))
            {
                propertyType = adaptedMember.ValueType;
                propertyValue = ObjectMemberAdapter.SafeGetValue(value, property.Name);
            }
            else
            {
                try
                {
                    propertyValue = property.GetValue(value);
                }
                catch (Exception exception)
                {
                    propertyValue = $"<unavailable: {exception.GetType().Name}>";
                }
            }

            members.Add(new ObjectInspectionMember(
                property.Name,
                MemberKind: "property",
                TypeName: propertyType.FullName ?? propertyType.Name,
                Display: _formatter.Format(propertyValue, previewOptions)));
        }

        foreach (var field in runtimeType
                     .GetFields(BindingFlags.Instance | BindingFlags.Public)
                     .OrderBy(field => field.Name, StringComparer.Ordinal))
        {
            if (members.Count >= memberLimit)
            {
                return members;
            }

            object? fieldValue;

            try
            {
                fieldValue = field.GetValue(value);
            }
            catch (Exception exception)
            {
                fieldValue = $"<unavailable: {exception.GetType().Name}>";
            }

            members.Add(new ObjectInspectionMember(
                field.Name,
                MemberKind: "field",
                TypeName: field.FieldType.FullName ?? field.FieldType.Name,
                Display: _formatter.Format(fieldValue, previewOptions)));
        }

        return members;
    }

    private IReadOnlyList<ObjectInspectionMember> GetRecordMembers(
        IReadOnlyList<KeyValuePair<string, object?>> fields,
        int memberLimit,
        ObjectFormattingOptions previewOptions)
    {
        return fields
            .Take(memberLimit)
            .Select(field => new ObjectInspectionMember(
                field.Key,
                MemberKind: "record",
                TypeName: field.Value?.GetType().FullName ?? "null",
                Display: _formatter.Format(field.Value, previewOptions)))
            .ToArray();
    }

    private IReadOnlyList<string> GetItemsPreview(object value, ObjectFormattingOptions previewOptions)
    {
        if (value is string || value is not IEnumerable enumerable)
        {
            return Array.Empty<string>();
        }

        if (TryGetCount(value) is null && value is not IList && value is not Array)
        {
            return Array.Empty<string>();
        }

        var previews = new List<string>();

        foreach (var item in enumerable)
        {
            if (previews.Count >= DefaultItemPreviewLimit)
            {
                break;
            }

            previews.Add(_formatter.Format(item, previewOptions));
        }

        return previews;
    }

    private static bool HasMoreItems(object value, int previewLimit)
    {
        var count = TryGetCount(value);
        return count.HasValue && count.Value > previewLimit;
    }

    private static int? TryGetCount(object value)
    {
        return value switch
        {
            null => null,
            Array array => array.Length,
            ICollection collection => collection.Count,
            _ => TryGetGenericCount(value),
        };
    }

    private static int? TryGetGenericCount(object value)
    {
        var runtimeType = value.GetType();
        var countProperty = runtimeType
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .FirstOrDefault(property =>
                property.Name == "Count" &&
                property.PropertyType == typeof(int) &&
                property.GetIndexParameters().Length == 0);

        if (countProperty is null)
        {
            return null;
        }

        try
        {
            return (int?)countProperty.GetValue(value);
        }
        catch
        {
            return null;
        }
    }
}
