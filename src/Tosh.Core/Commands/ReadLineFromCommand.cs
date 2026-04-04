namespace Tosh.Core.Commands;

public sealed class ReadLineFromCommand : ShellCommand
{
    public ReadLineFromCommand(string name = "read-line-from")
        : base(name, "Reads the next text line from an open managed file handle.", $"{name} [handle]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var (handle, _) = await StreamCommandUtilities.ResolveSingleReadableHandleAsync(context);
        var line = handle.ReadLine();

        if (line is not null)
        {
            yield return new ShellTextLine(line);
        }
    }
}
