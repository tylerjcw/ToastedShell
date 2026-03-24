using System.Reflection;

namespace Tosh.Core.Commands;

public sealed class ConstructorsCommand : ShellCommand
{
    public ConstructorsCommand()
        : base("constructors", "Lists public constructors for CLR types.", "constructors <type> [type ...]") { }

    public override IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var results = new List<object?>();

        foreach (var type in ReflectionMetadataUtilities.ResolveTypes(context, context.Arguments, allowInput: false))
        {
            foreach (var constructor in type.GetConstructors(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            {
                results.Add(new ProjectedObject(
                [
                    new ProjectedField("Type", "Type", ReflectionMetadataUtilities.GetDisplayName(type)),
                    new ProjectedField("ParameterCount", "ParameterCount", constructor.GetParameters().Length),
                    new ProjectedField("Signature", "Signature", ReflectionMetadataUtilities.FormatConstructorSignature(constructor)),
                ]));
            }
        }

        return AsyncEnumerableExtensions.FromEnumerable(results);
    }
}
