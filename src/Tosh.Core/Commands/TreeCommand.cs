using System.Diagnostics;

namespace Tosh.Core.Commands;

public sealed class TreeCommand : ShellCommand
{
    public TreeCommand()
        : base("tree", "Wraps the system tree utility, returning typed tree-entry objects.", "tree [-adfL level] [--show columns] [--hide columns] [--show-all] [path ...]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var resolvedPath = ResolveTreeExecutable(context);
        var parsedSelection = CommandDisplaySelectionParser.Parse(context.Arguments);

        if (!TryParseStructuredRequest(parsedSelection.RemainingArguments, out var request))
        {
            var external = new ExternalProcessCommand(Name, resolvedPath);

            await foreach (var item in external.ExecuteAsync(context))
            {
                yield return item;
            }

            yield break;
        }

        var result = await ExecuteTreeAsync(context, resolvedPath, request.ExternalArguments);

        context.Runtime.SetLastExitCode(result.ExitCode);
        context.PipelineExitStatusTracker?.Record(result.ExitCode);

        if (result.ExitCode != 0)
        {
            var message = string.IsNullOrWhiteSpace(result.StandardError)
                ? "The system 'tree' command failed."
                : result.StandardError.Trim();

            throw context.CreateDiagnostic(
                code: "tosh::runtime::tree_command_failed",
                title: message);
        }

        IReadOnlyList<TreeEntryInfo> entries;

        try
        {
            entries = TreeJsonParser.Parse(result.StandardOutput);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.Text.Json.JsonException)
        {
            throw context.CreateDiagnostic(
                code: "tosh::runtime::tree_json_parse_failed",
                title: $"Could not parse structured 'tree' output. {exception.Message}",
                help: "Try running the external `tree` command directly if you are using an output mode that does not support JSON.");
        }

        if (!string.IsNullOrWhiteSpace(result.StandardError))
        {
            await context.Runtime.Error.WriteLineAsync(result.StandardError.TrimEnd());
        }

        foreach (var entry in entries)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            yield return CommandDisplaySelectionParser.Apply(context.Runtime, parsedSelection.Selection, entry);
        }
    }

    private static string ResolveTreeExecutable(CommandContext context)
    {
        var lookup = ExternalCommandResolver.Resolve(context.Runtime.CurrentDirectory, "tree");

        return lookup.Status switch
        {
            ExternalCommandLookupStatus.Found when lookup.ResolvedPath is not null => lookup.ResolvedPath,
            _ => throw context.CreateDiagnostic(
                code: "tosh::runtime::tree_command_missing",
                title: "The system 'tree' command was not found.",
                help: "Install tree (e.g. 'pacman -S tree' or 'apt install tree') or invoke the external utility by full path."),
        };
    }

    private static bool TryParseStructuredRequest(
        IReadOnlyList<object?> arguments,
        out StructuredTreeRequest request)
    {
        var serialized = arguments
            .Select(ExternalTextSerializer.SerializeArgument)
            .ToArray();

        if (serialized.Any(IsUnsupportedStructuredOption))
        {
            request = null!;
            return false;
        }

        var externalArguments = new List<string>
        {
            "-J",
            "--du",
            "--timefmt", "%Y-%m-%dT%H:%M:%S",
            "-pugDs",
        };

        for (var index = 0; index < serialized.Length; index++)
        {
            var argument = serialized[index];

            switch (argument)
            {
                case "-J":
                case "--json":
                    break;
                case "-L":
                case "--level":
                    externalArguments.Add(argument);
                    if (index + 1 < serialized.Length)
                    {
                        externalArguments.Add(serialized[++index]);
                    }
                    break;
                case "-P":
                case "--pattern":
                case "-I":
                case "--exclude":
                    externalArguments.Add(argument);
                    if (index + 1 < serialized.Length)
                    {
                        externalArguments.Add(serialized[++index]);
                    }
                    break;
                default:
                    if (argument.StartsWith("--level=", StringComparison.Ordinal) ||
                        argument.StartsWith("--pattern=", StringComparison.Ordinal) ||
                        argument.StartsWith("--exclude=", StringComparison.Ordinal))
                    {
                        externalArguments.Add(argument);
                    }
                    else if (!IsSelectionOnlyOption(argument))
                    {
                        externalArguments.Add(argument);
                    }
                    break;
            }
        }

        request = new StructuredTreeRequest(externalArguments);
        return true;
    }

    private static bool IsSelectionOnlyOption(string argument)
    {
        return string.Equals(argument, "-J", StringComparison.Ordinal) ||
               string.Equals(argument, "--json", StringComparison.Ordinal);
    }

    private static bool IsUnsupportedStructuredOption(string argument)
    {
        return string.Equals(argument, "-h", StringComparison.Ordinal) ||
               string.Equals(argument, "--help", StringComparison.Ordinal) ||
               string.Equals(argument, "--version", StringComparison.Ordinal) ||
               string.Equals(argument, "-H", StringComparison.Ordinal) ||
               string.Equals(argument, "--html", StringComparison.Ordinal) ||
               string.Equals(argument, "-X", StringComparison.Ordinal) ||
               string.Equals(argument, "--xml", StringComparison.Ordinal);
    }

    private static async Task<TreeProcessResult> ExecuteTreeAsync(
        CommandContext context,
        string resolvedPath,
        IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = resolvedPath,
            WorkingDirectory = context.Runtime.CurrentDirectory,
            UseShellExecute = false,
            RedirectStandardInput = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true,
        };
        using var cancellationRegistration = context.CancellationToken.Register(() => TryKill(process));

        if (!process.Start())
        {
            throw context.CreateDiagnostic(
                code: "tosh::runtime::tree_command_start_failed",
                title: "Failed to start the system 'tree' command.");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(context.CancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(context.CancellationToken);

        await process.WaitForExitAsync(context.CancellationToken);

        return new TreeProcessResult(
            process.ExitCode,
            await stdoutTask,
            await stderrTask);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
    }

    private sealed record StructuredTreeRequest(IReadOnlyList<string> ExternalArguments);

    private sealed record TreeProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
