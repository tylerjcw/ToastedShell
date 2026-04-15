namespace Tosh.Core.Commands;

[CommandCategory("CLR")]
[CommandExample("echo [1, 2, 3] | cast list<int>")]
[CommandExample("echo 42 | cast string")]
[CommandNote("Cast converts to CLR target types, including constructed generic collection types like `list<int>`.")]
public sealed class CastCommand : ShellCommand
{
    public CastCommand()
        : base("cast", "Casts pipeline values to a CLR type, including constructed generic collection types.", "cast <type> [value ...]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (context.Arguments.Count == 0)
        {
            throw new InvalidOperationException("cast requires a target type.");
        }

        var targetType = ReflectionMetadataUtilities.ResolveType(context, context.Arguments[0]);
        IReadOnlyList<object?> inputs = context.Arguments.Count > 1
            ? context.Arguments.Skip(1).ToArray()
            : await AsyncEnumerableExtensions.ToListAsync(context.Input, context.CancellationToken);

        foreach (var input in inputs)
        {
            if (!TypeConversion.TryConvert(input, targetType, out var converted))
            {
                throw new InvalidOperationException($"Could not cast '{input}' to {ReflectionMetadataUtilities.GetDisplayName(targetType)}.");
            }

            yield return converted;
        }
    }
}
