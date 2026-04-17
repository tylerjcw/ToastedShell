namespace Tosh.Core.Commands;

[CommandCategory("Pipeline")]
[CommandOption("-a", "Include non-public and static members in the inspection.")]
[CommandOption("--flat", "Use flat text output instead of the interactive tree browser.")]
[CommandExample("ls -la | first | inspect", Title = "Inspect a file entry interactively")]
[CommandExample("new System.Random() | inspect -a", Title = "Inspect all members including private")]
[CommandExample("new System.Random() | inspect --flat", Title = "Flat output for scripting")]
[CommandNote("Inspect opens an inline tree browser for CLR values in interactive sessions. Use `-a` for non-public/static members, `--flat` for the legacy static inspection object output, and `i` inside the browser to insert the selected member text into the active REPL line at the cursor (or queue it for the next prompt when no line is active). In the REPL, `F2` tries to inspect the reference under the cursor, and `Alt+I` is available as a fallback on terminals that do not expose function keys cleanly.")]
[CommandOutput("An interactive tree view or flat member-value records depending on mode.")]
[PipelineInput(AcceptsScalar = true, AcceptsRecord = true, Description = "Inspects each piped object.")]
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
