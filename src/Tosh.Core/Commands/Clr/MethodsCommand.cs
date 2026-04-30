namespace Tosh.Core.Commands.Clr;

[Stdlib(StdlibCategory.Clr)]
[CommandCategory("CLR")]
[CommandArgument("type", "One or more type names or piped objects to inspect.", Required = false)]
[CommandExample("methods string", Title = "List String methods")]
[CommandExample("DateTime.Now | methods", Title = "List methods of a piped object")]
[CommandOutput("Method descriptor objects with Name, ReturnType, Parameters, and Signature properties.")]
[PipelineInput(AcceptsScalar = true, AcceptsRecord = true, Description = "Lists public methods of each piped object's type.")]
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
