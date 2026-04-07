namespace Tosh.Core.Commands;

[CommandCategory("Filesystem")]
public sealed class ReadToEndCommand : ShellCommand
{
    public ReadToEndCommand(string name = "read-to-end")
        : base(name, "Reads the remainder of an open managed file handle.", $"{name} [handle]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var (handle, _) = await StreamCommandUtilities.ResolveSingleReadableHandleAsync(context);

        if (handle.IsBinary)
        {
            yield return handle.ReadToEndBytes();
            yield break;
        }

        yield return handle.ReadToEndText();
    }
}
