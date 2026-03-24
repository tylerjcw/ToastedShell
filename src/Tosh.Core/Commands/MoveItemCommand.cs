namespace Tosh.Core.Commands;

public sealed class MoveItemCommand : ShellCommand
{
    public MoveItemCommand()
        : base("mv", "Moves or renames a file or directory.", "mv <source> <destination>") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var parsed = ParsedCommandArguments.Parse(context.Arguments);

        if (parsed.Positionals.Count != 2)
        {
            throw new InvalidOperationException("mv requires a source path and a destination path.");
        }

        var source = ShellPathArguments.Resolve(context.Runtime.CurrentDirectory, parsed.Positionals[0]);
        var destination = ShellPathArguments.Resolve(context.Runtime.CurrentDirectory, parsed.Positionals[1]);

        if (File.Exists(source))
        {
            var targetPath = Directory.Exists(destination)
                ? Path.Combine(destination, Path.GetFileName(source))
                : destination;

            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            File.Move(source, targetPath);
            yield return new FileInfo(targetPath);
            yield break;
        }

        if (Directory.Exists(source))
        {
            var targetPath = Directory.Exists(destination)
                ? Path.Combine(destination, Path.GetFileName(source))
                : destination;

            Directory.Move(source, targetPath);
            yield return new DirectoryInfo(targetPath);
            yield break;
        }

        throw new InvalidOperationException($"Source path '{source}' does not exist.");
    }
}
