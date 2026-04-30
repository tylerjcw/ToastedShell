using Tosh.Runtime;

namespace Tosh.Stdlib.Filesystem;

[Stdlib(StdlibCategory.Filesystem)]
[CommandCategory("Filesystem")]
[CommandArgument("path", "One or more directory paths to create.", TypeName = "path-like")]
[CommandOption("-p", "Create parent directories as needed; no error if existing.")]
[CommandOption("-v", "Print a message for each created directory.")]
[CommandOption("-m <mode>", "Set file mode (as in chmod), e.g. 755.")]
[CommandExample("mkdir newdir")]
[CommandExample("mkdir -p a/b/c", Title = "Create nested directories")]
[CommandOutput("Returns DirectoryInfo objects for each created directory.", TypeName = "DirectoryInfo", Members = "Name, FullName, Exists")]
[CommandSideEffects(WritesFiles = true)]
[PipelineInput(AcceptsList = true, Description = "Accepts piped path-like values.")]
public sealed class MakeDirectoryCommand : ShellCommand
{
    public MakeDirectoryCommand()
        : base("mkdir", "Creates one or more directories.", "mkdir [-pv] [-m mode] <path> [path...]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var options = ParseOptions(context.Arguments);
        var paths = await ShellPathArguments.CollectAsync(context, options.Positionals, context.CancellationToken);

        if (paths.Count == 0)
        {
            throw new InvalidOperationException("mkdir requires at least one path or pipeline input.");
        }

        foreach (var path in paths)
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            if (Directory.Exists(path))
            {
                if (!options.CreateParents)
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

            if (options.Mode is { } m && OperatingSystem.IsLinux())
            {
                File.SetUnixFileMode(path, m);
            }

            if (options.Verbose)
            {
                Console.Error.WriteLine($"mkdir: created directory '{path}'");
            }
        }
    }

    private static MkdirOptions ParseOptions(IReadOnlyList<object?> arguments)
    {
        var positionals = new List<object?>();
        var createParents = false;
        var verbose = false;
        UnixFileMode? mode = null;
        var parseOptions = true;

        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];

            if (!parseOptions || argument is not string text || text.Length == 0)
            {
                positionals.Add(argument);
                continue;
            }

            if (text == "--")
            {
                parseOptions = false;
                continue;
            }

            if (text.StartsWith("--", StringComparison.Ordinal))
            {
                switch (text[2..])
                {
                    case "parents":
                        createParents = true;
                        break;
                    case "verbose":
                        verbose = true;
                        break;
                    case var longOpt when longOpt.StartsWith("mode=", StringComparison.Ordinal):
                        mode = ParseOctalMode(longOpt["mode=".Length..]);
                        break;
                    case "mode":
                        if (++index >= arguments.Count || arguments[index] is not string modeText)
                        {
                            throw new InvalidOperationException("mkdir option '--mode' requires an octal value.");
                        }

                        mode = ParseOctalMode(modeText);
                        break;
                    default:
                        throw new InvalidOperationException($"Unsupported mkdir option '{text}'.");
                }

                continue;
            }

            if (text.StartsWith("-", StringComparison.Ordinal) && text.Length > 1)
            {
                for (var ci = 1; ci < text.Length; ci++)
                {
                    switch (text[ci])
                    {
                        case 'p':
                            createParents = true;
                            break;
                        case 'v':
                            verbose = true;
                            break;
                        case 'm':
                            var modeStr = ci + 1 < text.Length
                                ? text[(ci + 1)..]
                                : (++index < arguments.Count ? arguments[index]?.ToString() : null);

                            if (string.IsNullOrEmpty(modeStr))
                            {
                                throw new InvalidOperationException("mkdir option '-m' requires an octal value.");
                            }

                            mode = ParseOctalMode(modeStr);
                            ci = text.Length; // consumed rest of cluster
                            break;
                        default:
                            throw new InvalidOperationException($"Unsupported mkdir option '-{text[ci]}'.");
                    }
                }

                continue;
            }

            positionals.Add(argument);
        }

        return new MkdirOptions(createParents, verbose, mode, positionals);
    }

    private static UnixFileMode ParseOctalMode(string text)
    {
        var value = 0;

        foreach (var c in text)
        {
            if (c is < '0' or > '7')
            {
                throw new InvalidOperationException($"Invalid mode '{text}': expected octal digits (0-7).");
            }

            value = (value << 3) | (c - '0');
        }

        return (UnixFileMode)value;
    }

    private sealed record MkdirOptions(
        bool CreateParents,
        bool Verbose,
        UnixFileMode? Mode,
        IReadOnlyList<object?> Positionals);
}
