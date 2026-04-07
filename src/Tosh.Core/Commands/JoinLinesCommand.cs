namespace Tosh.Core.Commands;

[CommandCategory("Text")]
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
