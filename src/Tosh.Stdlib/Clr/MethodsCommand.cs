using System.Dynamic;
using Tosh.Runtime;

namespace Tosh.Stdlib.Clr;

[CommandCategory("CLR")]
[CommandArgument("subcommand-or-type", "Optional subcommand keyword (`has`, `get`) or a type name.", Required = false)]
[CommandArgument("name", "Operand for `has`/`get`: the method name to look up.", Required = false)]
[CommandExample("methods string", Title = "List all methods of String")]
[CommandExample("DateTime.Now | methods", Title = "List all methods of a piped object")]
[CommandExample("$obj | methods has ToString", Title = "Check whether $obj has a method named 'ToString'")]
[CommandExample("$obj | methods get ToString", Title = "Return the descriptor for method 'ToString' (or null)")]
[CommandOutput("Method descriptor objects with Name, ReturnType, Parameters, and Signature properties.")]
[PipelineInput(AcceptsScalar = true, AcceptsRecord = true, Description = "Lists or queries methods of each piped object's type.")]
public sealed class MethodsCommand : ShellCommand
{
    public MethodsCommand()
        : base("methods", "Lists or queries public methods for CLR types or pipeline objects.", "methods [has|get] [name] | methods [type ...]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (IntrospectionSubcommands.TryDispatch(context, out var subcommand, out var operand))
        {
            // For `methods`, only `has` and `get` make sense; the others (`props`/`fields`/`events`) don't apply.
            if (subcommand is not ("has" or "get"))
            {
                throw new InvalidOperationException(
                    $"methods: subcommand '{subcommand}' is not valid here. Use `members {subcommand}` instead.");
            }

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
            foreach (var method in ReflectionMetadataUtilities.EnumerateMethodProjections(type))
            {
                yield return method;
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
        if (operand is null)
        {
            throw new InvalidOperationException($"methods {subcommand}: requires a method name.");
        }

        var input = await AsyncEnumerableExtensions.ToListAsync(context.Input, context.CancellationToken);
        var trailing = context.Arguments.Skip(2).ToArray();
        var types = ResolveTargets(trailing, input, context);

        if (subcommand == "has")
        {
            yield return AnyMethod(types, operand);
            yield break;
        }

        // get: yield matching method descriptors.
        foreach (var type in types)
        {
            foreach (var m in ReflectionMetadataUtilities.EnumerateMethodProjections(type))
            {
                if (NameOf(m).Equals(operand, StringComparison.Ordinal))
                {
                    yield return m;
                }
            }
        }
    }

    private static bool AnyMethod(IReadOnlyList<object> types, string name)
    {
        foreach (var type in types)
        {
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

    private static string NameOf(ExpandoObject record)
    {
        var dict = (IDictionary<string, object?>)record;
        return dict.TryGetValue("Name", out var v) ? (v as string ?? string.Empty) : string.Empty;
    }
}
