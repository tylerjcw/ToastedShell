using System.Runtime.InteropServices;

namespace Tosh.Core.Commands;

[CommandCategory("Filesystem")]
[CommandArgument("source", "One or more source files or directories.", TypeName = "path-like")]
[CommandArgument("destination", "The target path.", TypeName = "path-like")]
[CommandOption("-r", "Copy directories recursively.")]
[CommandOption("-f", "Force overwrite of existing files.")]
[CommandOption("-n", "Do not overwrite existing files.")]
[CommandOption("-p", "Preserve file attributes such as timestamps.")]
[CommandOption("-u", "Only copy when the source is newer than the destination.")]
[CommandOption("-s", "Create symbolic links instead of copying files.")]
[CommandOption("-l", "Create hard links instead of copying files.")]
[CommandOption("-H", "Follow symbolic links on the command line (copy target, not link).")]
[CommandOption("-P", "Never follow symbolic links (copy the link itself). This is the default.")]
[CommandExample("cp file.txt backup.txt")]
[CommandExample("cp -r src/ dst/", Title = "Recursive directory copy")]
[CommandExample("cp -s file.txt link.txt", Title = "Create a symbolic link")]
[CommandExample("cp -l file.txt hardlink.txt", Title = "Create a hard link")]
[CommandOutput("Returns FileInfo or DirectoryInfo objects for each copied target.")]
[CommandSideEffects(ReadsFiles = true, WritesFiles = true)]
public sealed class CopyItemCommand : ShellCommand
{
    public CopyItemCommand()
        : base("cp", "Copies a file or directory.", "cp [-r] [-f] [-n] [-p] [-u] [-s] [-l] [-H] [-P] <source> [source ...] <destination>") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var parsed = ParsedCommandArguments.Parse(context.Arguments);
        var recursive = parsed.HasFlag("r", "R", "recursive");
        var force = parsed.HasFlag("f", "force");
        var noClobber = parsed.HasFlag("n", "no-clobber");
        var preserve = parsed.HasFlag("p", "preserve");
        var update = parsed.HasFlag("u", "update");
        var symlink = parsed.HasFlag("s", "symbolic-link");
        var hardlink = parsed.HasFlag("l", "link");
        var followArgLinks = parsed.HasFlag("H");
        var noFollowLinks = !followArgLinks || parsed.HasFlag("P", "no-dereference");

        if (parsed.Positionals.Count < 2)
        {
            throw new InvalidOperationException("cp requires at least one source path and a destination path.");
        }

        var sources = ShellPathArguments.ExpandMany(context.Runtime.CurrentDirectory, parsed.Positionals.Take(parsed.Positionals.Count - 1).ToArray());
        var destinationMatches = ShellPathArguments.Expand(context.Runtime.CurrentDirectory, parsed.Positionals[^1]);

        if (destinationMatches.Count != 1)
        {
            throw new InvalidOperationException("cp destination must resolve to exactly one path.");
        }

        var destination = destinationMatches[0];

        if (sources.Count > 1 && !Directory.Exists(destination))
        {
            throw new InvalidOperationException("When copying multiple sources, the destination must be an existing directory.");
        }

        if (symlink && hardlink)
        {
            throw new InvalidOperationException("Cannot use both -s (symbolic link) and -l (hard link) at the same time.");
        }

        foreach (var source in sources)
        {
            if (File.Exists(source) || IsSymlink(source))
            {
                var effectiveSource = ResolveSourcePath(source, followArgLinks, noFollowLinks);
                var targetPath = ResolveCopyDestination(source, destination);

                if (ShouldSkipCopy(effectiveSource, targetPath, noClobber, update))
                {
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

                if (symlink)
                {
                    File.CreateSymbolicLink(targetPath, Path.GetFullPath(effectiveSource));
                }
                else if (hardlink)
                {
                    CreateHardLink(Path.GetFullPath(effectiveSource), targetPath);
                }
                else if (IsSymlink(source) && noFollowLinks)
                {
                    var linkTarget = File.ResolveLinkTarget(source, returnFinalTarget: false)?.ToString()
                        ?? throw new InvalidOperationException($"Cannot read symlink target of '{source}'.");
                    File.CreateSymbolicLink(targetPath, linkTarget);
                }
                else
                {
                    File.Copy(effectiveSource, targetPath, overwrite: force || !noClobber);
                }

                if (preserve && !symlink)
                {
                    PreserveAttributes(effectiveSource, targetPath);
                }

                yield return new FileInfo(targetPath);
                continue;
            }

            if (Directory.Exists(source))
            {
                if (!recursive)
                {
                    throw new InvalidOperationException("Copying directories requires -r.");
                }

                var targetPath = Directory.Exists(destination)
                    ? Path.Combine(destination, Path.GetFileName(source))
                    : destination;

                CopyDirectory(source, targetPath, force, noClobber, update, preserve, symlink, hardlink, noFollowLinks);
                yield return new DirectoryInfo(targetPath);
                continue;
            }

            throw new InvalidOperationException($"Source path '{source}' does not exist.");
        }
    }

    private static bool ShouldSkipCopy(string source, string target, bool noClobber, bool update)
    {
        if (!File.Exists(target))
        {
            return false;
        }

        if (noClobber)
        {
            return true;
        }

        if (update)
        {
            return File.GetLastWriteTimeUtc(source) <= File.GetLastWriteTimeUtc(target);
        }

        return false;
    }

    private static void PreserveAttributes(string source, string target)
    {
        var sourceInfo = new FileInfo(source);
        var targetInfo = new FileInfo(target);
        targetInfo.LastWriteTimeUtc = sourceInfo.LastWriteTimeUtc;
        targetInfo.LastAccessTimeUtc = sourceInfo.LastAccessTimeUtc;
        targetInfo.CreationTimeUtc = sourceInfo.CreationTimeUtc;

        if (!OperatingSystem.IsWindows())
        {
            try
            {
                File.SetUnixFileMode(target, File.GetUnixFileMode(source));
            }
            catch (IOException)
            {
                // Best-effort on permission preservation
            }
        }
    }

    private static string ResolveCopyDestination(string source, string destination)
    {
        return Directory.Exists(destination)
            ? Path.Combine(destination, Path.GetFileName(source))
            : destination;
    }

    private static string ResolveSourcePath(string source, bool followArgLinks, bool noFollowLinks)
    {
        if (IsSymlink(source) && followArgLinks && !noFollowLinks)
        {
            return File.ResolveLinkTarget(source, returnFinalTarget: true)?.ToString() ?? source;
        }

        return source;
    }

    private static bool IsSymlink(string path)
    {
        var info = new FileInfo(path);
        return info.Exists && info.LinkTarget is not null;
    }

    private static void CopyDirectory(string source, string destination, bool force, bool noClobber, bool update, bool preserve, bool symlink, bool hardlink, bool noFollowLinks)
    {
        Directory.CreateDirectory(destination);

        foreach (var file in Directory.EnumerateFiles(source))
        {
            var targetFile = Path.Combine(destination, Path.GetFileName(file));

            if (ShouldSkipCopy(file, targetFile, noClobber, update))
            {
                continue;
            }

            if (symlink)
            {
                File.CreateSymbolicLink(targetFile, Path.GetFullPath(file));
            }
            else if (hardlink)
            {
                CreateHardLink(Path.GetFullPath(file), targetFile);
            }
            else if (IsSymlink(file) && noFollowLinks)
            {
                var linkTarget = File.ResolveLinkTarget(file, returnFinalTarget: false)?.ToString();

                if (linkTarget is not null)
                {
                    File.CreateSymbolicLink(targetFile, linkTarget);
                }
            }
            else
            {
                File.Copy(file, targetFile, overwrite: force || !noClobber);
            }

            if (preserve && !symlink)
            {
                PreserveAttributes(file, targetFile);
            }
        }

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            var targetDirectory = Path.Combine(destination, Path.GetFileName(directory));
            CopyDirectory(directory, targetDirectory, force, noClobber, update, preserve, symlink, hardlink, noFollowLinks);
        }
    }

    private static void CreateHardLink(string existingPath, string newLinkPath)
    {
        if (OperatingSystem.IsWindows())
        {
            if (!CreateHardLinkWindows(newLinkPath, existingPath, IntPtr.Zero))
            {
                throw new IOException($"Failed to create hard link '{newLinkPath}' pointing to '{existingPath}'.");
            }
        }
        else
        {
            if (LinkUnix(existingPath, newLinkPath) != 0)
            {
                throw new IOException($"Failed to create hard link '{newLinkPath}' pointing to '{existingPath}': {Marshal.GetLastPInvokeErrorMessage()}");
            }
        }
    }

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int LinkUnix(string oldpath, string newpath);

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkWindows(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);
}
