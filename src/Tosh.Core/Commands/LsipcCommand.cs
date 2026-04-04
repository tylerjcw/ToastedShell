using System.Diagnostics;
using System.Dynamic;

namespace Tosh.Core.Commands;

public sealed class LsipcCommand : ShellCommand
{
    public LsipcCommand()
        : base("lsipc", "Wraps the system lsipc utility, returning structured IPC resource and limit records for JSON-backed invocations.", "lsipc [-m|-M|-q|-Q|-s|-S] [-g] [-i id|-N name] [-c] [-t] [-b] [-P] [-o columns]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var resolvedPath = ResolveLsipcExecutable(context);
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

        var result = await ExecuteStructuredLsipcAsync(context, resolvedPath, request);

        context.Runtime.SetLastExitCode(result.ExitCode);
        context.PipelineExitStatusTracker?.Record(result.ExitCode);

        if (result.ExitCode != 0)
        {
            var message = string.IsNullOrWhiteSpace(result.StandardError)
                ? "The system 'lsipc' command failed."
                : result.StandardError.Trim();

            throw context.CreateDiagnostic(
                code: "tosh::runtime::lsipc_command_failed",
                title: message);
        }

        if (!string.IsNullOrWhiteSpace(result.StandardError))
        {
            await context.Runtime.Error.WriteLineAsync(result.StandardError.TrimEnd());
        }

        IReadOnlyList<ExpandoObject> rows;

        try
        {
            rows = LsipcJsonParser.ParseRows(result.StandardOutput);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.Text.Json.JsonException)
        {
            throw context.CreateDiagnostic(
                code: "tosh::runtime::lsipc_json_parse_failed",
                title: $"Could not parse structured 'lsipc' output. {exception.Message}",
                help: "Try running the external `lsipc` command directly if you are using an output mode that does not support JSON.");
        }

        foreach (var row in rows)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            yield return CommandDisplaySelectionParser.Apply(context.Runtime, parsedSelection.Selection, row);
        }
    }

    private static string ResolveLsipcExecutable(CommandContext context)
    {
        var lookup = ExternalCommandResolver.Resolve(context.Runtime.CurrentDirectory, "lsipc");

        return lookup.Status switch
        {
            ExternalCommandLookupStatus.Found when lookup.ResolvedPath is not null => lookup.ResolvedPath,
            _ => throw context.CreateDiagnostic(
                code: "tosh::runtime::lsipc_command_missing",
                title: "The system 'lsipc' command was not found.",
                help: "Install util-linux or invoke the external utility by full path once it is available."),
        };
    }

    private static bool TryParseStructuredRequest(
        IReadOnlyList<object?> arguments,
        out IReadOnlyList<string> externalArguments)
    {
        externalArguments = Array.Empty<string>();

        var serialized = arguments
            .Select(ExternalTextSerializer.SerializeArgument)
            .ToArray();

        if (serialized.Any(IsUnsupportedStructuredOption))
        {
            return false;
        }

        var normalized = new List<string> { "-J" };
        var hasTimeFormat = false;

        for (var index = 0; index < serialized.Length; index++)
        {
            var argument = serialized[index];

            switch (argument)
            {
                case "-m":
                case "--shmems":
                case "-M":
                case "--posix-shmems":
                case "-q":
                case "--queues":
                case "-Q":
                case "--posix-mqueues":
                case "-s":
                case "--semaphores":
                case "-S":
                case "--posix-semaphores":
                case "-g":
                case "--global":
                case "-c":
                case "--creator":
                case "-t":
                case "--time":
                case "-b":
                case "--bytes":
                case "-P":
                case "--numeric-perms":
                case "-l":
                case "--list":
                    normalized.Add(argument);
                    break;
                case "-i":
                case "--id":
                case "-N":
                case "--name":
                case "-o":
                case "--output":
                case "--time-format":
                    normalized.Add(argument);

                    if (index + 1 >= serialized.Length)
                    {
                        throw new InvalidOperationException($"Option '{argument}' requires a value.");
                    }

                    var value = serialized[++index];
                    normalized.Add(value);

                    if (string.Equals(argument, "--time-format", StringComparison.Ordinal))
                    {
                        hasTimeFormat = true;
                    }

                    break;
                default:
                    if (argument.StartsWith("--time-format=", StringComparison.Ordinal))
                    {
                        hasTimeFormat = true;
                        normalized.Add(argument);
                        break;
                    }

                    if (argument.StartsWith("--output=", StringComparison.Ordinal))
                    {
                        normalized.Add(argument);
                        break;
                    }

                    if (argument.StartsWith("--id=", StringComparison.Ordinal) ||
                        argument.StartsWith("--name=", StringComparison.Ordinal))
                    {
                        normalized.Add(argument);
                        break;
                    }

                    return false;
            }
        }

        if (!hasTimeFormat)
        {
            normalized.Add("--time-format=iso");
        }

        externalArguments = normalized;
        return true;
    }

    private static bool IsUnsupportedStructuredOption(string argument)
    {
        return string.Equals(argument, "--noheadings", StringComparison.Ordinal) ||
               string.Equals(argument, "--notruncate", StringComparison.Ordinal) ||
               string.Equals(argument, "-e", StringComparison.Ordinal) ||
               string.Equals(argument, "--export", StringComparison.Ordinal) ||
               string.Equals(argument, "-n", StringComparison.Ordinal) ||
               string.Equals(argument, "--newline", StringComparison.Ordinal) ||
               string.Equals(argument, "-r", StringComparison.Ordinal) ||
               string.Equals(argument, "--raw", StringComparison.Ordinal) ||
               string.Equals(argument, "-y", StringComparison.Ordinal) ||
               string.Equals(argument, "--shell", StringComparison.Ordinal) ||
               string.Equals(argument, "-h", StringComparison.Ordinal) ||
               string.Equals(argument, "--help", StringComparison.Ordinal) ||
               string.Equals(argument, "-V", StringComparison.Ordinal) ||
               string.Equals(argument, "--version", StringComparison.Ordinal);
    }

    private static async Task<LsipcProcessResult> ExecuteStructuredLsipcAsync(
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
                code: "tosh::runtime::lsipc_command_start_failed",
                title: "Failed to start the system 'lsipc' command.");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(context.CancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(context.CancellationToken);

        await process.WaitForExitAsync(context.CancellationToken);

        return new LsipcProcessResult(
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

    private sealed record LsipcProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
