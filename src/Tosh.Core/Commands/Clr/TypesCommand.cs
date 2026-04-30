namespace Tosh.Core.Commands.Clr;

[Stdlib(StdlibCategory.Clr)]
[CommandCategory("CLR")]
[CommandArgument("filter", "Optional name or pattern to filter types.", Required = false)]
[CommandOption("-a", "Include all loaded assemblies, not just commonly used types.")]
[CommandExample("types System.String", Title = "Search for String type")]
[CommandExample("types list", Title = "Search for list-like types")]
[CommandExample("types map | where _.Namespace == ToSh", Title = "Filter to ToSh namespace")]
[CommandNote("Types searches both CLR types and ToSh shell types like `list`, `array`, `dict`, `table`, and `tuple`.")]
[CommandOutput("Type descriptor objects with Name, Namespace, and Assembly properties.")]
public sealed class TypesCommand : ShellCommand
{
    public TypesCommand()
        : base("types", "Lists available CLR and ToSh shell types.", "types [-a] [filter]") { }

    public override IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        return ExecuteCoreAsync(context);
    }

    private static async IAsyncEnumerable<object?> ExecuteCoreAsync(CommandContext context)
    {
        var parsed = ParsedCommandArguments.Parse(context.Arguments);
        var includeNonPublic = parsed.HasFlag("a", "all");
        var filter = parsed.Positionals.Count > 0 ? parsed.Positionals[0]?.ToString() : null;

        if (string.IsNullOrWhiteSpace(filter))
        {
            var pipedFilter = await TextInputUtilities.ReadScalarValuesFromInputAsync(context, allowEmpty: true);
            filter = string.Join(" ", pipedFilter).Trim();
        }

        var clrResults = TypeCatalog.GetAllTypes(includeNonPublic)
            .Where(type => string.IsNullOrWhiteSpace(filter) ||
                           (type.FullName?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false) ||
                           type.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .Select(ReflectionMetadataUtilities.CreateTypeProjection)
            .Cast<object?>();

        var shellResults = context.Runtime.Classes
            .Where(entry => entry.Value is IShellTypeDescriptor)
            .GroupBy(entry => ((IShellTypeDescriptor)entry.Value!).ShellFullName, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var descriptor = (IShellTypeDescriptor)group.First().Value!;
                var aliases = group
                    .Select(entry => entry.Key)
                    .Where(name => !string.Equals(name, descriptor.ShellTypeName, StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                return new
                {
                    Descriptor = descriptor,
                    Aliases = aliases,
                };
            })
            .Where(entry => string.IsNullOrWhiteSpace(filter) ||
                            entry.Descriptor.ShellTypeName.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                            entry.Descriptor.ShellFullName.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                            (entry.Descriptor.ShellNamespace?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false) ||
                            entry.Aliases.Any(alias => alias.Contains(filter, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(entry => entry.Descriptor.ShellTypeName, StringComparer.OrdinalIgnoreCase)
            .Select(entry => ReflectionMetadataUtilities.CreateTypeProjection(entry.Descriptor))
            .Cast<object?>();

        foreach (var result in shellResults.Concat(clrResults))
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            yield return result;
        }
    }
}
