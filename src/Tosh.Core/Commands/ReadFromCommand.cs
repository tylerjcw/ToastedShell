namespace Tosh.Core.Commands;

[CommandCategory("Filesystem")]
public sealed class ReadFromCommand : ShellCommand
{
    public ReadFromCommand(string name = "read-from")
        : base(name, "Reads a text or binary chunk from an open managed file handle.", $"{name} [handle] [count]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var (handle, count) = await StreamCommandUtilities.ResolveSingleReadableHandleAsync(context);
        var readCount = count ?? StreamCommandUtilities.DefaultReadChunkSize;

        if (handle.IsBinary)
        {
            var bytes = handle.ReadBytes(readCount);

            if (bytes.Length > 0)
            {
                yield return bytes;
            }

            yield break;
        }

        var text = handle.ReadText(readCount);

        if (text.Length > 0)
        {
            yield return text;
        }
    }
}
