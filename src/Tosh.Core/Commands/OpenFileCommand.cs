namespace Tosh.Core.Commands;

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
