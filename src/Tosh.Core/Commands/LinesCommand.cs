namespace Tosh.Core.Commands;

[CommandCategory("Text")]
[CommandArgument("text ...", "Text values to split. When omitted, reads pipeline input.", Required = false)]
[CommandExample("lines \"alpha\\nbeta\"", Title = "Split an explicit string")]
[CommandExample("read-file notes.txt | lines | where _.Contains(\"TODO\")", Title = "Split file text into lines")]
public sealed class LinesCommand : ShellCommand
{
    public LinesCommand()
        : base("lines", "Splits text input into individual lines.", "lines [text ...]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (context.Arguments.Count > 0)
        {
            foreach (var argument in context.Arguments)
            {
                var text = argument switch
                {
                    ShellTextLine line => line.Text,
                    string s => s,
                    _ => ExternalTextSerializer.Serialize(argument),
                };

                foreach (var line in SplitLines(text))
                {
                    context.CancellationToken.ThrowIfCancellationRequested();
                    yield return new ShellTextLine(line);
                }
            }

            yield break;
        }

        await foreach (var item in context.Input.WithCancellation(context.CancellationToken))
        {
            var text = item switch
            {
                ShellTextLine line => line.Text,
                string s => s,
                _ => ExternalTextSerializer.Serialize(item),
            };

            foreach (var line in SplitLines(text))
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                yield return new ShellTextLine(line);
            }
        }
    }

    private static IEnumerable<string> SplitLines(string text)
    {
        if (!text.Contains('\n') && !text.Contains('\r'))
        {
            if (text.Length > 0)
            {
                yield return text;
            }

            yield break;
        }

        using var reader = new StringReader(text);

        while (reader.ReadLine() is { } line)
        {
            yield return line;
        }
    }
}
