namespace Tosh.Core.Commands;

[CommandCategory("CLR")]
[CommandExample("members string")]
[CommandExample("DateTime.Now | members")]
public sealed class MembersCommand : ShellCommand
{
    public MembersCommand()
        : base("members", "Lists public members for CLR types or pipeline objects.", "members [type ...]") { }

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
            foreach (var member in ReflectionMetadataUtilities.EnumerateMemberProjections(type))
            {
                yield return member;
            }
        }
    }
}
