namespace Tosh.Core.Commands;

public sealed class MakeDirectoryCommand : ShellCommand
{
    public MakeDirectoryCommand()
        : base("mkdir", "Creates one or more directories.", "mkdir [-p] <path> [path...]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var parsed = ParsedCommandArguments.Parse(context.Arguments);
        var createParents = parsed.HasFlag("p", "parents");
        var paths = await ShellPathArguments.CollectAsync(context, parsed.Positionals, context.CancellationToken);

        if (paths.Count == 0)
        {
            throw new InvalidOperationException("mkdir requires at least one path or pipeline input.");
        }

        foreach (var path in paths)
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            if (Directory.Exists(path))
            {
                if (!createParents)
                {
                    throw new InvalidOperationException($"Directory '{path}' already exists.");
                }

                yield return new DirectoryInfo(path);
                continue;
            }

            if (File.Exists(path))
            {
                throw new InvalidOperationException($"Cannot create directory '{path}' because a file already exists there.");
            }

            yield return Directory.CreateDirectory(path);
        }
    }
}
