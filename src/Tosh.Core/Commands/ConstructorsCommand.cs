namespace Tosh.Core.Commands;

[CommandCategory("CLR")]
[CommandExample("constructors System.String | first 5")]
[CommandExample("constructors list<int>")]
[CommandExample("constructors dict<string, int>")]
[CommandNote("Constructors works with CLR types, ToSh named types, and shell collection types like `list<int>` and `dict<string, int>`.")]
public sealed class ConstructorsCommand : ShellCommand
{
    public ConstructorsCommand()
        : base("constructors", "Lists constructors for CLR types, ToSh classes, and shell collection types.", "constructors [type ...]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var typeTargets = context.Arguments.Count > 0
            ? ReflectionMetadataUtilities.ResolveTypeLikeTargets(context, context.Arguments)
            : (await AsyncEnumerableExtensions.ToListAsync(context.Input, context.CancellationToken))
                .Select(item => ReflectionMetadataUtilities.ResolveTypeLikeTarget(context, item ?? typeof(object)))
                .DistinctBy(type => type is Type clrType
                    ? clrType.AssemblyQualifiedName ?? clrType.FullName ?? clrType.Name
                    : ((IShellTypeDescriptor)type).ShellFullName,
                    StringComparer.OrdinalIgnoreCase)
                .ToArray();

        foreach (var type in typeTargets)
        {
            foreach (var constructor in ReflectionMetadataUtilities.EnumerateConstructorProjections(type))
            {
                yield return constructor;
            }
        }
    }
}
