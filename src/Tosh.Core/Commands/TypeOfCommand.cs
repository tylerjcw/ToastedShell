namespace Tosh.Core.Commands;

[CommandCategory("CLR")]
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
