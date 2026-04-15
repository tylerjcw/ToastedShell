namespace Tosh.Core.Commands;

[CommandCategory("Filesystem")]
[CommandArgument("path ...", "One or more file paths to open.", Required = false, TypeName = "path-like")]
[CommandOption("--read, -r", "Open for reading. This is the default.")]
[CommandOption("--write, -w", "Open for writing and replace any previous contents.")]
[CommandOption("--append, -a", "Open for writing at the end of the file.")]
[CommandOption("--binary, -b", "Open a binary handle instead of a text handle.")]
[CommandOption("--encoding <name>", "Use a specific text encoding for text writers.")]
[CommandExample("open-file ./notes.txt")]
[CommandExample("open-file --write ./notes.txt")]
[CommandExample("open-file --binary --append ./data.bin")]
[CommandNote("Open-file is the start of ToSh's managed stream system. It returns explicit text or binary handle objects instead of hiding file resources behind implicit properties. Active handles are also visible through `$tosh.Session.OpenHandles` and `$tosh.Session.OpenHandleCount`.")]
[CommandOutput("Returns managed file-handle objects that can be passed to `read-from`, `read-line-from`, `read-to-end`, `write-to`, `write-line-to`, `flush`, `close`, `seek`, `position`, `length`, and `copy-to`.")]
[PipelineInput(AcceptsList = true, Description = "Consumes piped path-like input when explicit file paths are omitted.")]
public sealed class OpenFileCommand : ShellCommand
{
    public OpenFileCommand(string name = "open-file")
        : base(name, "Opens one or more files as managed text or binary handles.", $"{name} [--read|--write|--append] [--binary] [--encoding <name>] <path> [path...]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var options = ParseOptions(context.Arguments);
        var paths = await ShellPathArguments.CollectAsync(context, options.Positionals, context.CancellationToken);

        if (paths.Count == 0)
        {
            throw new InvalidOperationException($"{Name} requires at least one path or pipeline input.");
        }

        foreach (var path in paths)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            yield return OpenHandle(path, options);
        }
    }

    private static ManagedFileHandle OpenHandle(string path, OpenFileOptions options)
    {
        if (options.Binary)
        {
            return options.Mode switch
            {
                "write" => ManagedFileHandle.OpenBinaryWrite(path, append: false),
                "append" => ManagedFileHandle.OpenBinaryWrite(path, append: true),
                _ => ManagedFileHandle.OpenBinaryRead(path),
            };
        }

        var encoding = StreamCommandUtilities.ResolveEncoding(options.EncodingName);

        return options.Mode switch
        {
            "write" => ManagedFileHandle.OpenTextWrite(path, append: false, encoding),
            "append" => ManagedFileHandle.OpenTextWrite(path, append: true, encoding),
            _ => ManagedFileHandle.OpenTextRead(path),
        };
    }

    private static OpenFileOptions ParseOptions(IReadOnlyList<object?> arguments)
    {
        var positionals = new List<object?>();
        var binary = false;
        string? encodingName = null;
        var mode = "read";

        for (var index = 0; index < arguments.Count; index++)
        {
            var text = arguments[index]?.ToString();

            if (string.IsNullOrWhiteSpace(text))
            {
                positionals.Add(arguments[index]);
                continue;
            }

            switch (text)
            {
                case "--binary":
                case "-b":
                    binary = true;
                    continue;
                case "--read":
                case "-r":
                    mode = "read";
                    continue;
                case "--write":
                case "-w":
                    mode = "write";
                    continue;
                case "--append":
                case "-a":
                    mode = "append";
                    continue;
                case "--encoding" when index + 1 < arguments.Count:
                    encodingName = arguments[++index]?.ToString();
                    continue;
                case string option when option.StartsWith("-", StringComparison.Ordinal):
                    throw new InvalidOperationException($"Unsupported {nameof(OpenFileCommand).Replace("Command", string.Empty).ToLowerInvariant()} option '{option}'.");
                default:
                    positionals.Add(arguments[index]);
                    break;
            }
        }

        if (binary && encodingName is not null)
        {
            throw new InvalidOperationException("Binary file handles do not use a text encoding.");
        }

        return new OpenFileOptions(positionals, mode, binary, encodingName);
    }

    private sealed record OpenFileOptions(
        IReadOnlyList<object?> Positionals,
        string Mode,
        bool Binary,
        string? EncodingName);
}
