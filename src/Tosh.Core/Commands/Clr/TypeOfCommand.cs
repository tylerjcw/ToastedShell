namespace Tosh.Core.Commands.Clr;

[Stdlib(StdlibCategory.Clr)]
[CommandCategory("CLR")]
[CommandArgument("value", "One or more values to inspect. If omitted, reads from the pipeline.", Required = false)]
[CommandExample("type-of 42", Title = "Get the type of a number")]
[CommandExample("\"hello\" | type-of", Title = "Get the type of a piped value")]
[CommandExample("ls | first | type-of", Title = "Get the type of a file system entry")]
[CommandOutput("The CLR Type or ToSh class descriptor for each input value.")]
[PipelineInput(AcceptsScalar = true, AcceptsRecord = true, Description = "Returns the type of each piped object.")]
public sealed class TypeOfCommand : ShellCommand
{
    public TypeOfCommand()
        : base("type-of", "Returns the CLR type or ToSh class type for each input object.", "type-of [value...]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        await using var enumerator = context.Input.GetAsyncEnumerator(context.CancellationToken);

        if (await enumerator.MoveNextAsync())
        {
            do
            {
                yield return GetTypeValue(enumerator.Current);
            }
            while (await enumerator.MoveNextAsync());

            yield break;
        }

        foreach (var argument in context.Arguments)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            yield return GetTypeValue(argument);
        }
    }

    private static object? GetTypeValue(object? value)
    {
        return value switch
        {
            null => null,
            IShellTypeDescriptor descriptor => descriptor,
            IShellTypedObject typed => typed.ShellTypeDescriptor,
            _ when BuiltInShellTypes.TryDescribeRuntimeValue(value, out var descriptor) => descriptor,
            _ => value.GetType(),
        };
    }
}
