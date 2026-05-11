using System.Dynamic;
using Tosh.Runtime;

namespace Tosh.Stdlib.Clr;

[CommandCategory("CLR")]
[CommandArgument("subcommand-or-type", "Optional subcommand keyword (`has`, `get`, `props`, `fields`, `methods`, `events`) or a type name.", Required = false)]
[CommandArgument("name", "Operand for `has`/`get`: the member name to look up.", Required = false)]
[CommandExample("members string", Title = "List all members of String")]
[CommandExample("DateTime.Now | members", Title = "List all members of a piped object")]
[CommandExample("$obj | members has Name", Title = "Check whether $obj has a member named 'Name'")]
[CommandExample("$obj | members get FullName", Title = "Return the descriptor for member 'FullName' (or null)")]
[CommandExample("$obj | members props", Title = "Filter to properties only")]
[CommandExample("$obj | members fields", Title = "Filter to fields only")]
[CommandExample("$obj | members methods", Title = "Filter to methods only")]
[CommandOutput("Member descriptor objects with Name, Kind, MemberType, and other reflection metadata.")]
[PipelineInput(AcceptsScalar = true, AcceptsRecord = true, Description = "Inspects the type of each piped object.")]
public sealed class MembersCommand : ShellCommand
{
    public MembersCommand()
        : base("members", "Lists or queries members for CLR types or pipeline objects.", "members [has|get|props|fields|methods|events] [name] | members [type ...]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (IntrospectionSubcommands.TryDispatch(context, out var subcommand, out var operand))
        {
            await foreach (var result in ExecuteSubcommandAsync(context, subcommand!, operand))
            {
                yield return result;
            }

            yield break;
        }

        var input = await AsyncEnumerableExtensions.ToListAsync(context.Input, context.CancellationToken);
        var types = ResolveTargets(context.Arguments, input, context);

        foreach (var type in types)
        {
            foreach (var member in ReflectionMetadataUtilities.EnumerateMemberProjections(type))
            {
                yield return member;
            }
        }
    }

    private static IReadOnlyList<object> ResolveTargets(IReadOnlyList<object?> arguments, IReadOnlyList<object?> input, CommandContext context)
    {
        if (arguments.Count > 0)
        {
            return ReflectionMetadataUtilities.ResolveTypeLikeTargets(context, arguments);
        }

        return input
            .Select(item => ReflectionMetadataUtilities.ResolveTypeLikeTarget(context, item ?? typeof(object)))
            .DistinctBy(type => type is Type clrType
                ? clrType.AssemblyQualifiedName ?? clrType.FullName ?? clrType.Name
                : ((IShellTypeDescriptor)type).ShellFullName,
                StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private async IAsyncEnumerable<object?> ExecuteSubcommandAsync(
        CommandContext context,
        string subcommand,
        string? operand)
    {
        var input = await AsyncEnumerableExtensions.ToListAsync(context.Input, context.CancellationToken);
        var skip = operand is null ? 1 : 2;
        var trailing = context.Arguments.Skip(skip).ToArray();
        var types = ResolveTargets(trailing, input, context);

        switch (subcommand)
        {
            case "has":
                if (operand is null)
                {
                    throw new InvalidOperationException("members has: requires a member name.");
                }

                yield return AnyMember(types, operand, includeMethods: true);
                yield break;

            case "get":
                if (operand is null)
                {
                    throw new InvalidOperationException("members get: requires a member name.");
                }

                foreach (var match in FindMembers(types, operand, includeMethods: true))
                {
                    yield return match;
                }

                yield break;

            case "props":
                foreach (var type in types)
                {
                    foreach (var m in ReflectionMetadataUtilities.EnumerateMemberProjections(type))
                    {
                        if (KindOf(m) is "Property")
                        {
                            yield return m;
                        }
                    }
                }

                yield break;

            case "fields":
                foreach (var type in types)
                {
                    foreach (var m in ReflectionMetadataUtilities.EnumerateMemberProjections(type))
                    {
                        if (KindOf(m) is "Field")
                        {
                            yield return m;
                        }
                    }
                }

                yield break;

            case "methods":
                foreach (var type in types)
                {
                    foreach (var m in ReflectionMetadataUtilities.EnumerateMethodProjections(type))
                    {
                        yield return m;
                    }
                }

                yield break;

            case "events":
                yield break;

            default:
                throw new InvalidOperationException($"members: unknown subcommand '{subcommand}'.");
        }
    }

    private static bool AnyMember(IReadOnlyList<object> types, string name, bool includeMethods)
    {
        foreach (var type in types)
        {
            foreach (var m in ReflectionMetadataUtilities.EnumerateMemberProjections(type))
            {
                if (NameOf(m).Equals(name, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            if (!includeMethods)
            {
                continue;
            }

            foreach (var m in ReflectionMetadataUtilities.EnumerateMethodProjections(type))
            {
                if (NameOf(m).Equals(name, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static IEnumerable<ExpandoObject> FindMembers(IReadOnlyList<object> types, string name, bool includeMethods)
    {
        foreach (var type in types)
        {
            foreach (var m in ReflectionMetadataUtilities.EnumerateMemberProjections(type))
            {
                if (NameOf(m).Equals(name, StringComparison.Ordinal))
                {
                    yield return m;
                }
            }

            if (!includeMethods)
            {
                continue;
            }

            foreach (var m in ReflectionMetadataUtilities.EnumerateMethodProjections(type))
            {
                if (NameOf(m).Equals(name, StringComparison.Ordinal))
                {
                    yield return m;
                }
            }
        }
    }

    private static string KindOf(ExpandoObject record)
    {
        var dict = (IDictionary<string, object?>)record;
        return dict.TryGetValue("Kind", out var v) ? (v as string ?? string.Empty) : string.Empty;
    }

    private static string NameOf(ExpandoObject record)
    {
        var dict = (IDictionary<string, object?>)record;
        return dict.TryGetValue("Name", out var v) ? (v as string ?? string.Empty) : string.Empty;
    }
}
