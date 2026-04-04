namespace Tosh.Core.Commands;

public sealed class WriteLineToCommand : ShellCommand
{
    public WriteLineToCommand(string name = "write-line-to")
        : base(name, "Writes one or more text lines to an open managed text file handle.", $"{name} <handle> [value...]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (context.Arguments.Count == 0)
        {
            throw new InvalidOperationException($"{Name} requires a file handle as its first argument.");
        }

        var handle = StreamCommandUtilities.ResolveHandle(context.Arguments[0]);

        if (handle.IsBinary)
        {
            throw new InvalidOperationException($"{Name} only works with text file handles.");
        }

        var values = CommandArguments.Slice(context.Arguments, 1);

        if (values.Count > 0)
        {
            var rendered = await FileIoUtilities.RenderTextPayloadAsync(context, values);
            handle.WriteTextLine(rendered);
            yield break;
        }

        var inputValues = await AsyncEnumerableExtensions.ToListAsync(context.Input, context.CancellationToken);

        if (inputValues.Count == 0)
        {
            handle.WriteTextLine(string.Empty);
            yield break;
        }

        foreach (var item in inputValues)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            handle.WriteTextLine(ExternalTextSerializer.Serialize(item));
        }
    }
}
