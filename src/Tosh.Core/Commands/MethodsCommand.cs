using System.Reflection;

namespace Tosh.Core.Commands;

public sealed class MethodsCommand : ShellCommand
{
    public MethodsCommand()
        : base("methods", "Lists public methods for CLR types or pipeline objects.", "methods [type ...]") { }

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
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                         .Where(method => !method.IsSpecialName)
                         .OrderBy(method => method.Name, StringComparer.OrdinalIgnoreCase))
            {
                yield return new ProjectedObject(
                [
                    new ProjectedField("Type", "Type", ReflectionMetadataUtilities.GetDisplayName(type)),
                    new ProjectedField("Name", "Name", method.Name),
                    new ProjectedField("ReturnType", "ReturnType", ReflectionMetadataUtilities.GetDisplayName(method.ReturnType)),
                    new ProjectedField("Static", "Static", method.IsStatic),
                    new ProjectedField("ParameterCount", "ParameterCount", method.GetParameters().Length),
                    new ProjectedField("Signature", "Signature", ReflectionMetadataUtilities.FormatMethodSignature(method)),
                ]);
            }
        }
    }
}
