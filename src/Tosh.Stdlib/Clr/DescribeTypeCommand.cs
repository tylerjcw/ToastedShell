using Tosh.Runtime;

namespace Tosh.Stdlib.Clr;

[Stdlib(StdlibCategory.Clr)]
[CommandCategory("CLR")]
[CommandArgument("type", "One or more type names or piped objects to describe.", Required = false)]
[CommandExample("describe-type string", Title = "Describe the String type")]
[CommandExample("describe-type list dict table", Title = "Describe multiple shell types")]
[CommandExample("42 | describe-type", Title = "Describe the type of a piped value")]
[CommandOutput("Type projection objects with hierarchy, interfaces, properties, methods, and constructors.")]
[PipelineInput(AcceptsScalar = true, AcceptsRecord = true, Description = "Describes the type of each piped object.")]
public sealed class DescribeTypeCommand : ShellCommand
{
    public DescribeTypeCommand()
        : base("describe-type", "Describes CLR types, ToSh named types, or shell collection types.", "describe-type [type ...]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (context.Arguments.Count > 0)
        {
            foreach (var type in ReflectionMetadataUtilities.ResolveTypeLikeTargets(context, context.Arguments))
            {
                yield return ReflectionMetadataUtilities.CreateTypeProjection(type);
            }

            yield break;
        }

        await foreach (var item in context.Input.WithCancellation(context.CancellationToken))
        {
            yield return ReflectionMetadataUtilities.CreateTypeProjection(
                ReflectionMetadataUtilities.ResolveTypeLikeTarget(context, item ?? typeof(object)));
        }
    }
}
