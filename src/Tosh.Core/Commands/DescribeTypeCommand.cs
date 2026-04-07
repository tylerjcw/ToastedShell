namespace Tosh.Core.Commands;

[CommandCategory("CLR")]
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
