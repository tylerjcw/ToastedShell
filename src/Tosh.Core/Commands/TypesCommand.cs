namespace Tosh.Core.Commands;

public sealed class TypesCommand : ShellCommand
{
    public TypesCommand()
        : base("types", "Lists available CLR types.", "types [-a] [filter]") { }

    public override IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var parsed = ParsedCommandArguments.Parse(context.Arguments);
        var includeNonPublic = parsed.HasFlag("a", "all");
        var filter = parsed.Positionals.Count > 0 ? parsed.Positionals[0]?.ToString() : null;

        var results = TypeCatalog.GetAllTypes(includeNonPublic)
            .Where(type => string.IsNullOrWhiteSpace(filter) ||
                           (type.FullName?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false) ||
                           type.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .Select(ReflectionMetadataUtilities.CreateTypeProjection)
            .Cast<object?>()
            .ToArray();

        return AsyncEnumerableExtensions.FromEnumerable(results);
    }
}
