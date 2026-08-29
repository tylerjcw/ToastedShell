using Tosh.Runtime;

namespace Tosh.Stdlib.Clr;

/// <summary>Top-level shortcut for <c>members props</c>.</summary>
[CommandCategory("CLR")]
[CommandArgument("type", "Optional type name. When omitted, uses piped objects.", Required = false)]
[CommandExample("string | props", Title = "List public properties of String")]
[CommandExample("$obj | props", Title = "List public properties of a piped object")]
[CommandOutput("Property descriptor objects (subset of `members` output, filtered to Kind == Property).")]
[PipelineInput(AcceptsScalar = true, AcceptsRecord = true, Description = "Inspects the type of each piped object.")]
public sealed class PropsCommand : ShellCommand
{
    public PropsCommand()
        : base("props", "Lists public properties for CLR types or pipeline objects (shortcut for `members props`).", "props [type ...]") { }

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
            foreach (var member in ReflectionMetadataUtilities.EnumerateMemberProjections(type))
            {
                var dict = (IDictionary<string, object?>)member;
                if (dict.TryGetValue("Kind", out var kind) && kind is "Property")
                {
                    yield return member;
                }
            }
        }
    }
}
