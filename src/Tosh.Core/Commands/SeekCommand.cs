using System.IO;

namespace Tosh.Core.Commands;

public sealed class SeekCommand : ShellCommand
{
    public SeekCommand()
        : base("seek", "Moves an open managed file handle to a new position and returns the handle for continued piping.", "seek [handle] <offset> [begin|current|end]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var (handle, remainingArguments) = await StreamCommandUtilities.ResolveSingleHandleAndArgumentsAsync(context);

        if (remainingArguments.Count == 0)
        {
            throw new InvalidOperationException($"{Name} requires an offset.");
        }

        var offset = CommandArguments.RequireConverted<long>(remainingArguments, 0, "offset");
        var origin = remainingArguments.Count > 1
            ? StreamCommandUtilities.ParseSeekOrigin(remainingArguments[1])
            : SeekOrigin.Begin;

        handle.Seek(offset, origin);
        yield return handle;
    }
}
