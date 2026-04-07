namespace Tosh.Core.Commands;

[CommandCategory("Pipeline")]
public sealed class InspectCommand : ShellCommand
{
    public InspectCommand()
        : base("inspect", "Inspects piped CLR objects inline, or returns legacy flat output with --flat.", "inspect [-a] [--flat]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var parsed = ParsedCommandArguments.Parse(context.Arguments);
        var includeAllMembers = parsed.HasFlag("a", "all");
        var flat = parsed.HasFlag("flat");
        var provider = flat ? null : context.Runtime.InlinePrompts;

        await using var enumerator = context.Input.GetAsyncEnumerator(context.CancellationToken);

        if (!await enumerator.MoveNextAsync())
        {
            throw new InvalidOperationException("inspect expects pipeline input.");
        }

        var index = 1;

        do
        {
            if (provider is not null)
            {
                provider.Inspect(enumerator.Current, includeAllMembers);
            }
            else
            {
                yield return context.Runtime.Inspector.Inspect(enumerator.Current, index, includeAllMembers);
            }

            index++;
        }
        while (await enumerator.MoveNextAsync());
    }
}
