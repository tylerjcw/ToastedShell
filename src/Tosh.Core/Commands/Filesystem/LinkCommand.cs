using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Tosh.Core.Commands.Filesystem;

[Stdlib(StdlibCategory.Filesystem)]
[CommandCategory("Filesystem")]
[CommandArgument("target", "The target path that the link will point to.", TypeName = "path-like")]
[CommandArgument("link-path", "The path where the link will be created.", TypeName = "path-like")]
[CommandOption("-s", "Create a symbolic link instead of a hard link.")]
[CommandOption("-f", "Remove existing destination files.")]
[CommandExample("ln original.txt hardlink.txt")]
[CommandExample("ln -s /usr/bin/python3 ./python", Title = "Create symbolic link")]
[CommandOutput("Returns a FileSystemEntry for the created link.")]
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
            if (!Interop.CreateHardLink(linkPath, target, IntPtr.Zero))
            {
                throw new InvalidOperationException(
                    $"Unable to create hard link '{linkPath}': {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
            }

            return;
        }

        if (Interop.link(target, linkPath) != 0)
        {
            throw new InvalidOperationException($"Unable to create hard link '{linkPath}'.");
        }
    }

    private static class Interop
    {
        [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);

        [DllImport("libc", SetLastError = true)]
        public static extern int link(string existingPath, string newPath);
    }
}
