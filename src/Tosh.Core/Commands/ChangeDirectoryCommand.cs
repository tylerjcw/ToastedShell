namespace Tosh.Core.Commands;

public sealed class ChangeDirectoryCommand : ShellCommand
{
    public ChangeDirectoryCommand()
        : base("cd", "Changes the current directory for the Tosh session.", "cd [path | - | +]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var parsed = ParsedCommandArguments.Parse(context.Arguments);
        string path;

        if (parsed.Positionals.Count > 0)
        {
            path = CommandArguments.RequireString(parsed.Positionals, 0, "path");
        }
        else
        {
            var pipedPaths = await ShellPathArguments.CollectAsync(context, Array.Empty<object?>(), context.CancellationToken);
            path = pipedPaths.Count == 0 ? PathUtilities.UserHomeDirectory : pipedPaths[0];
        }

        var oldDirectory = FileSystemEntry.From(new DirectoryInfo(context.Runtime.CurrentDirectory));

        if (path == "-")
        {
            var previous = context.Runtime.GoBack();

            if (previous is null)
            {
                throw new InvalidOperationException("No previous directory in the stack.");
            }

            context.Runtime.CurrentDirectory = previous;
            var newEntry = FileSystemEntry.From(new DirectoryInfo(previous));
            await RaiseDirectoryChangedAsync(context, oldDirectory, newEntry);
            yield return newEntry;
            yield break;
        }

        if (path == "+")
        {
            var next = context.Runtime.GoForward();

            if (next is null)
            {
                throw new InvalidOperationException("No next directory in the stack.");
            }

            context.Runtime.CurrentDirectory = next;
            var newEntry = FileSystemEntry.From(new DirectoryInfo(next));
            await RaiseDirectoryChangedAsync(context, oldDirectory, newEntry);
            yield return newEntry;
            yield break;
        }

        var resolvedPaths = ShellPathArguments.Expand(context.Runtime.CurrentDirectory, path);

        if (resolvedPaths.Count != 1)
        {
            throw new InvalidOperationException("cd requires a single path.");
        }

        var resolvedPath = resolvedPaths[0];

        DirectoryInfo directoryInfo;

        try
        {
            directoryInfo = new DirectoryInfo(resolvedPath);

            if (!directoryInfo.Exists)
            {
                throw new InvalidOperationException($"Directory '{resolvedPath}' does not exist.");
            }

            context.Runtime.CurrentDirectory = directoryInfo.FullName;
            context.Runtime.PushDirectory(directoryInfo.FullName);
        }
        catch (IOException exception)
        {
            throw new InvalidOperationException($"Cannot change to directory '{resolvedPath}': {exception.Message}");
        }
        catch (UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"Permission denied: '{resolvedPath}'.");
        }

        var entry = FileSystemEntry.From(directoryInfo);
        await RaiseDirectoryChangedAsync(context, oldDirectory, entry);
        yield return entry;
    }

    private static async Task RaiseDirectoryChangedAsync(CommandContext context, FileSystemEntry oldDirectory, FileSystemEntry newDirectory)
    {
        var sender = context.Runtime.EventSenderFactory?.Invoke()
            ?? new ShellEventSender(Function: null, Script: null, Line: null);
        var evt = new DirectoryChangedEvent(oldDirectory, newDirectory, sender);
        await context.Runtime.Events.RaiseAsync(evt, context.CancellationToken);
    }
}
