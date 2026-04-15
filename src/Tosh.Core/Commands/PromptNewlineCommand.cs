namespace Tosh.Core.Commands;

[CommandCategory("Prompt")]
[CommandExample("prompt-newline")]
public sealed class PromptNewlineCommand : ShellCommand
{
    public PromptNewlineCommand()
        : base("prompt-newline", "Inserts a line break in the prompt.", "prompt-newline") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        await Task.CompletedTask;
        yield return new StyledText("\n");
    }
}