namespace Tosh.Core.Commands;

public sealed class DescribeTypeCommand : ShellCommand
{
    public DescribeTypeCommand()
        : base("describe-type", "Describes CLR types for input objects or explicit type names.", "describe-type [type ...]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (context.Arguments.Count > 0)
        {
            foreach (var type in ReflectionMetadataUtilities.ResolveTypes(context, context.Arguments))
            {
                yield return ReflectionMetadataUtilities.CreateTypeProjection(type);
            }

            yield break;
        }

        await foreach (var item in context.Input.WithCancellation(context.CancellationToken))
        {
            yield return ReflectionMetadataUtilities.CreateTypeProjection(item?.GetType() ?? typeof(object));
        }
    }
}
