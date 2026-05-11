using Tosh.Runtime;

namespace Tosh.Stdlib.Clr;

/// <summary>Top-level shortcut for <c>methods</c>. Provided for symmetry with <c>props</c>.</summary>
[CommandCategory("CLR")]
[CommandArgument("type", "Optional type name. When omitted, uses piped objects.", Required = false)]
[CommandExample("string | funcs", Title = "List public methods of String")]
[CommandExample("$obj | funcs", Title = "List public methods of a piped object")]
[CommandOutput("Method descriptor objects (alias for `methods`).")]
[PipelineInput(AcceptsScalar = true, AcceptsRecord = true, Description = "Inspects the type of each piped object.")]
public sealed class FuncsCommand : ShellCommand
{
    public FuncsCommand()
        : base("funcs", "Lists public methods for CLR types or pipeline objects (shortcut for `methods`).", "funcs [type ...]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var input = await AsyncEnumerableExtensions.ToListAsync(context.Input, context.CancellationToken);
        var types = context.Arguments.Count > 0
            ? ReflectionMetadataUtilities.ResolveTypeLikeTargets(context, context.Arguments)
            : input
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
