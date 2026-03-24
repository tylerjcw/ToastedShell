namespace Tosh.Core.Commands;

public sealed class SplitCommand : ShellCommand
{
    public SplitCommand()
        : base("split", "Splits text values into smaller text values.", "split [delimiter] [text ...]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var parsed = ParsedCommandArguments.Parse(context.Arguments);
        var delimiter = parsed.Positionals.Count > 0
            ? parsed.Positionals[0]?.ToString() ?? string.Empty
            : null;
        IReadOnlyList<object?> inputValues = parsed.Positionals.Count > 1
            ? parsed.Positionals.Skip(1).ToArray()
            : await AsyncEnumerableExtensions.ToListAsync(context.Input, context.CancellationToken);

        foreach (var value in inputValues)
        {
            var text = value switch
            {
                ShellTextLine line => line.Text,
                _ => ExternalTextSerializer.Serialize(value),
            };

            IEnumerable<string> parts = delimiter is null
                ? text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                : text.Split([delimiter], StringSplitOptions.None);

            foreach (var part in parts)
            {
                yield return new ShellTextLine(part);
            }
        }
    }
}
