namespace Tosh.Core.Commands;

public sealed class InspectCommand : ShellCommand
{
    public InspectCommand()
        : base("inspect", "Inspects piped CLR objects and returns their shape and preview data.", "inspect [-a]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var parsed = ParsedCommandArguments.Parse(context.Arguments);
        var includeAllMembers = parsed.HasFlag("a", "all");

        await using var enumerator = context.Input.GetAsyncEnumerator(context.CancellationToken);

        if (!await enumerator.MoveNextAsync())
        {
            throw new InvalidOperationException("inspect expects pipeline input.");
        }

        var index = 1;

        do
        {
            yield return context.Runtime.Inspector.Inspect(enumerator.Current, index, includeAllMembers);
            index++;
        }
        while (await enumerator.MoveNextAsync());
    }
}
