namespace Tosh.Core.Commands;

public sealed class TypeOfCommand : ShellCommand
{
    public TypeOfCommand()
        : base("type-of", "Returns the CLR type for each input object.", "type-of [value...]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        await using var enumerator = context.Input.GetAsyncEnumerator(context.CancellationToken);

        if (await enumerator.MoveNextAsync())
        {
            do
            {
                yield return enumerator.Current?.GetType();
            }
            while (await enumerator.MoveNextAsync());

            yield break;
        }

        foreach (var argument in context.Arguments)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            yield return argument?.GetType();
        }
    }
}
