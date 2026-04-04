namespace Tosh.Core.Commands;

public sealed class WriteToCommand : ShellCommand
{
    public WriteToCommand(string name = "write-to")
        : base(name, "Writes plain text or bytes to an open managed file handle.", $"{name} <handle> [value...]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (context.Arguments.Count == 0)
        {
            throw new InvalidOperationException($"{Name} requires a file handle as its first argument.");
        }

        var handle = StreamCommandUtilities.ResolveHandle(context.Arguments[0]);
        var values = CommandArguments.Slice(context.Arguments, 1);

        if (handle.IsBinary)
        {
            var bytes = await FileIoUtilities.ReadBytePayloadAsync(context, values);

            if (bytes.Length > 0)
            {
                handle.WriteBytes(bytes);
            }

            yield break;
        }

        var text = await FileIoUtilities.RenderTextPayloadAsync(context, values);

        if (text.Length > 0)
        {
            handle.WriteText(text);
        }
    }
}
