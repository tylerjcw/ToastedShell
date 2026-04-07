namespace Tosh.Core.Commands;

[CommandCategory("CLR")]
public sealed class MethodsCommand : ShellCommand
{
    public MethodsCommand()
        : base("methods", "Lists public methods for CLR types or pipeline objects.", "methods [type ...]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var types = context.Arguments.Count > 0
            ? ReflectionMetadataUtilities.ResolveTypeLikeTargets(context, context.Arguments)
            : (await AsyncEnumerableExtensions.ToListAsync(context.Input, context.CancellationToken))
                .Select(item => ReflectionMetadataUtilities.ResolveTypeLikeTarget(context, item ?? typeof(object)))
                .DistinctBy(type => type is Type clrType
                    ? clrType.AssemblyQualifiedName ?? clrType.FullName ?? clrType.Name
                    : ((IShellTypeDescriptor)type).ShellFullName,
                    StringComparer.OrdinalIgnoreCase)
                .ToArray();

        foreach (var type in types)
        {
            foreach (var method in ReflectionMetadataUtilities.EnumerateMethodProjections(type))
            {
                yield return method;
            }
        }
    }
}
