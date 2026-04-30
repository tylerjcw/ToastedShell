namespace Tosh.Core.Commands.Text;

[Stdlib(StdlibCategory.Text)]
[CommandCategory("Text")]
[CommandArgument("separator", "Separator inserted between values. Defaults to the platform newline.", Required = false)]
[CommandExample("echo alpha beta gamma | join-lines \", \"", Title = "Join values with a comma")]
[CommandExample("read-lines names.txt | join-lines", Title = "Join lines with newlines")]
[CommandOutput("A single string formed by concatenating input lines with the configured separator.")]
public sealed class JoinLinesCommand : ShellCommand
{
    public JoinLinesCommand()
        : base("join-lines", "Joins input values into a single text value.", "join-lines [separator]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var separator = context.Arguments.Count > 0
            ? context.Arguments[0]?.ToString() ?? string.Empty
            : Environment.NewLine;
        var values = await AsyncEnumerableExtensions.ToListAsync(context.Input, context.CancellationToken);
        var parts = values.Select(value => value is ShellTextLine line ? line.Text : ExternalTextSerializer.Serialize(value));
        yield return new ShellTextLine(string.Join(separator, parts));
    }
}
