namespace Tosh.Core.Commands;

public sealed class ChangeDirectoryCommand : ShellCommand
{
    public ChangeDirectoryCommand()
        : base("cd", "Changes the current directory for the Tosh session.", "cd [path]") { }

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

        var resolvedPath = PathUtilities.ResolvePath(context.Runtime.CurrentDirectory, path);

        DirectoryInfo directoryInfo;

        try
        {
            directoryInfo = new DirectoryInfo(resolvedPath);

            if (!directoryInfo.Exists)
            {
                throw new InvalidOperationException($"Directory '{resolvedPath}' does not exist.");
            }

            context.Runtime.CurrentDirectory = directoryInfo.FullName;
        }
        catch (IOException exception)
        {
            throw new InvalidOperationException($"Cannot change to directory '{resolvedPath}': {exception.Message}");
        }
        catch (UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"Permission denied: '{resolvedPath}'.");
        }

        yield return directoryInfo;
    }
}
