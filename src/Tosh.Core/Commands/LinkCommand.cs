using System.Runtime.InteropServices;

namespace Tosh.Core.Commands;

public sealed class LinkCommand : ShellCommand
{
    public LinkCommand()
        : base("ln", "Creates hard links or symbolic links.", "ln [-s] [-f] <target> <link-path>") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var parsed = ParsedCommandArguments.Parse(context.Arguments);
        var symbolic = parsed.HasFlag("s", "symbolic");
        var force = parsed.HasFlag("f", "force");

        if (parsed.Positionals.Count != 2)
        {
            throw new InvalidOperationException("ln requires a target path and a link path.");
        }

        var targetMatches = ShellPathArguments.Expand(context.Runtime.CurrentDirectory, parsed.Positionals[0]);
        var linkPathMatches = ShellPathArguments.Expand(context.Runtime.CurrentDirectory, parsed.Positionals[1]);

        if (targetMatches.Count != 1 || linkPathMatches.Count != 1)
        {
            throw new InvalidOperationException("ln requires exactly one target path and one link path.");
        }

        var target = targetMatches[0];
        var linkPath = linkPathMatches[0];

        if (force)
        {
            if (File.Exists(linkPath))
            {
                File.Delete(linkPath);
            }
            else if (Directory.Exists(linkPath))
            {
                Directory.Delete(linkPath, recursive: true);
            }
        }

        FileSystemInfo created;

        if (symbolic)
        {
            created = Directory.Exists(target)
                ? Directory.CreateSymbolicLink(linkPath, target)
                : File.CreateSymbolicLink(linkPath, target);
        }
        else
        {
            if (!File.Exists(target))
            {
                throw new InvalidOperationException("Hard links currently require an existing file target.");
            }

            CreateHardLink(linkPath, target);
            created = new FileInfo(linkPath);
        }

        yield return FileSystemEntry.From(created, preferLongDisplay: true);
    }

    private static void CreateHardLink(string linkPath, string target)
    {
        if (OperatingSystem.IsWindows())
        {
            throw new InvalidOperationException("Hard links are not supported by the built-in 'ln' command on Windows yet.");
        }

        if (Interop.link(target, linkPath) != 0)
        {
            throw new InvalidOperationException($"Unable to create hard link '{linkPath}'.");
        }
    }

    private static class Interop
    {
        [DllImport("libc", SetLastError = true)]
        public static extern int link(string existingPath, string newPath);
    }
}
