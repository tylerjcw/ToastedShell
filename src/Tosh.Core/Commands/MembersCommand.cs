using System.Reflection;

namespace Tosh.Core.Commands;

public sealed class MembersCommand : ShellCommand
{
    public MembersCommand()
        : base("members", "Lists public members for CLR types or pipeline objects.", "members [type ...]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var types = context.Arguments.Count > 0
            ? ReflectionMetadataUtilities.ResolveTypes(context, context.Arguments)
            : (await AsyncEnumerableExtensions.ToListAsync(context.Input, context.CancellationToken))
                .Select(item => item?.GetType() ?? typeof(object))
                .DistinctBy(type => type.AssemblyQualifiedName ?? type.FullName ?? type.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();

        foreach (var type in types)
        {
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                         .Where(property => property.GetIndexParameters().Length == 0))
            {
                yield return new ProjectedObject(
                [
                    new ProjectedField("Type", "Type", ReflectionMetadataUtilities.GetDisplayName(type)),
                    new ProjectedField("Name", "Name", property.Name),
                    new ProjectedField("Kind", "Kind", "Property"),
                    new ProjectedField("MemberType", "MemberType", ReflectionMetadataUtilities.GetDisplayName(property.PropertyType)),
                    new ProjectedField("Static", "Static", (property.GetMethod ?? property.SetMethod)?.IsStatic ?? false),
                    new ProjectedField("Writable", "Writable", property.CanWrite),
                ]);
            }

            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            {
                yield return new ProjectedObject(
                [
                    new ProjectedField("Type", "Type", ReflectionMetadataUtilities.GetDisplayName(type)),
                    new ProjectedField("Name", "Name", field.Name),
                    new ProjectedField("Kind", "Kind", "Field"),
                    new ProjectedField("MemberType", "MemberType", ReflectionMetadataUtilities.GetDisplayName(field.FieldType)),
                    new ProjectedField("Static", "Static", field.IsStatic),
                    new ProjectedField("Writable", "Writable", !(field.IsInitOnly || field.IsLiteral)),
                ]);
            }
        }
    }
}
