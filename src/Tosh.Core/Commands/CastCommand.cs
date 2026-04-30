namespace Tosh.Core.Commands;

[Stdlib(StdlibCategory.Clr)]
[CommandCategory("CLR")]
[CommandArgument("type", "The target CLR type to cast to, including generic types like list<int>.")]
[CommandArgument("value", "Optional value(s) to cast. If omitted, reads from the pipeline.", Required = false)]
[CommandExample("echo [1, 2, 3] | cast list<int>", Title = "Cast an array to a typed list")]
[CommandExample("echo 42 | cast string", Title = "Cast a number to a string")]
[CommandNote("Cast converts to CLR target types, including constructed generic collection types like `list<int>`.")]
[CommandOutput("The cast values in the target type.")]
[PipelineInput(AcceptsScalar = true, AcceptsList = true, Description = "Casts each piped value to the target type.")]
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
