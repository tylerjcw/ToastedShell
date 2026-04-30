using System.Diagnostics;

using Tosh.Runtime;

namespace Tosh.Stdlib.Sys;

[Stdlib(StdlibCategory.Sys)]
[CommandCategory("System")]
[CommandArgument("[query ...]", "Structured journal queries accept common `journalctl` filters such as match expressions, unit filters, priorities, boot selectors, and time windows.", Required = false)]
[CommandOption("-n <count>|--lines <count>", "Limit the structured result set to the most recent entries.")]
[CommandOption("-u <unit>|--unit <unit>", "Restrict structured output to a specific systemd unit.")]
[CommandOption("--since <when>", "Restrict entries to those at or after the supplied time.")]
[CommandOption("--until <when>", "Restrict entries to those at or before the supplied time.")]
[CommandOption("-p <range>|--priority <range>", "Restrict entries to a priority or priority range.")]
[CommandOption("-b [id]|--boot [id]", "Restrict entries to the current boot or a selected boot.")]
[CommandOption("-g <pattern>|--grep <pattern>", "Restrict structured output to entries whose messages match a pattern.")]
[CommandOption("--user", "Query the user journal instead of the system journal.")]
[CommandOption("--system", "Query the system journal explicitly.")]
[CommandOption("--show <columns>", "Use ToSh display-only column selection on the structured journal-entry rows.")]
[CommandOption("--hide <columns>", "Hide display columns while preserving the underlying structured journal entries.")]
[CommandOption("--show-all", "Expose every selectable structured journal-entry display column.")]
[CommandExample("journalctl -n 20", Title = "Stream the newest journal entries as structured objects")]
[CommandExample("journalctl -u sshd.service | first 10", Title = "Inspect a unit's logs in the pipeline")]
[CommandExample("journalctl --since yesterday | where _.Priority <= 3", Title = "Filter recent high-priority entries")]
[CommandOutput("Streams structured journal-entry objects from `journalctl -o json` for supported non-follow query paths. Unsupported or text-oriented modes fall back to the native utility.")]
[PipelineInput(Description = "The structured `journalctl` builtin is explicit-arg-first and does not currently consume pipeline input.")]
public sealed class JournalctlCommand : ShellCommand
{
    public JournalctlCommand()
        : base("journalctl", "Wraps journalctl, returning typed journal-entry objects for supported JSON-backed query invocations.", "journalctl [-n count] [-u unit] [--since when] [--until when] [query ...]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (OperatingSystem.IsWindows())
        {
            throw context.CreateDiagnostic(
                code: "tosh.runtime.command_windows_unavailable",
                title: $"'{Name}' is not available on Windows.",
                help: "This command requires the systemd journal, which is a Linux-only service.");
        }

        var resolvedPath = ResolveExecutable(context);
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

        await foreach (var entry in ExecuteStructuredQueryAsync(context, resolvedPath, request.ExternalArguments, parsedSelection.Selection))
        {
            yield return entry;
        }
    }

    private static string ResolveExecutable(CommandContext context)
    {
        var lookup = ExternalCommandResolver.Resolve(context.Runtime.CurrentDirectory, "journalctl");

        return lookup.Status switch
        {
            ExternalCommandLookupStatus.Found when lookup.ResolvedPath is not null => lookup.ResolvedPath,
            _ => throw context.CreateDiagnostic(
                code: "tosh.runtime.journalctl_command_missing",
                title: "The system 'journalctl' command was not found.",
                help: "Install systemd or invoke the external utility by full path once it is available."),
        };
    }

    private static bool TryParseStructuredRequest(
        IReadOnlyList<object?> arguments,
        out StructuredJournalctlRequest request)
    {
        var serialized = arguments
            .Select(ExternalTextSerializer.SerializeArgument)
            .ToArray();

        if (serialized.Any(IsUnsupportedStructuredOption))
        {
            request = null!;
            return false;
        }

        var externalArguments = serialized.ToList();

        if (ContainsOutputOption(serialized))
        {
            request = null!;
            return false;
        }

        externalArguments.Add("-o");
        externalArguments.Add("json");

        if (!externalArguments.Any(argument => string.Equals(argument, "--no-pager", StringComparison.Ordinal)))
        {
            externalArguments.Add("--no-pager");
        }

        request = new StructuredJournalctlRequest(externalArguments);
        return true;
    }

    private static bool ContainsOutputOption(IReadOnlyList<string> arguments)
    {
        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];

            if (string.Equals(argument, "-o", StringComparison.Ordinal) ||
                string.Equals(argument, "--output", StringComparison.Ordinal))
            {
                return true;
            }

            if (argument.StartsWith("--output=", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsUnsupportedStructuredOption(string argument)
    {
        return string.Equals(argument, "-f", StringComparison.Ordinal) ||
               string.Equals(argument, "--follow", StringComparison.Ordinal) ||
               string.Equals(argument, "--header", StringComparison.Ordinal) ||
               string.Equals(argument, "--cursor", StringComparison.Ordinal) ||
               string.Equals(argument, "--after-cursor", StringComparison.Ordinal) ||
               string.Equals(argument, "--show-cursor", StringComparison.Ordinal) ||
               string.Equals(argument, "-N", StringComparison.Ordinal) ||
               string.Equals(argument, "--fields", StringComparison.Ordinal) ||
               string.Equals(argument, "-F", StringComparison.Ordinal) ||
               string.Equals(argument, "--field", StringComparison.Ordinal) ||
               string.Equals(argument, "--list-boots", StringComparison.Ordinal) ||
               string.Equals(argument, "--disk-usage", StringComparison.Ordinal) ||
               string.Equals(argument, "--vacuum-size", StringComparison.Ordinal) ||
               string.Equals(argument, "--vacuum-time", StringComparison.Ordinal) ||
               string.Equals(argument, "--vacuum-files", StringComparison.Ordinal) ||
               string.Equals(argument, "--sync", StringComparison.Ordinal) ||
               string.Equals(argument, "--relinquish-var", StringComparison.Ordinal) ||
               string.Equals(argument, "--smart-relinquish-var", StringComparison.Ordinal) ||
               string.Equals(argument, "--flush", StringComparison.Ordinal) ||
               string.Equals(argument, "--rotate", StringComparison.Ordinal) ||
               string.Equals(argument, "--verify", StringComparison.Ordinal) ||
               string.Equals(argument, "--setup-keys", StringComparison.Ordinal);
    }

    private static async IAsyncEnumerable<object?> ExecuteStructuredQueryAsync(
        CommandContext context,
        string resolvedPath,
        IReadOnlyList<string> arguments,
        DisplayColumnSelection selection)
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
                code: "tosh.runtime.journalctl_command_start_failed",
                title: "Failed to start the system 'journalctl' command.");
        }

        var stderrTask = PumpStandardErrorAsync(process, context.Runtime.Error, context.CancellationToken);

        try
        {
            while (true)
            {
                var line = await process.StandardOutput.ReadLineAsync(context.CancellationToken);

                if (line is null)
                {
                    break;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                SystemdJournalEntry entry;

                try
                {
                    entry = JournalctlJsonParser.ParseLine(line);
                }
                catch (Exception exception) when (exception is InvalidOperationException or System.Text.Json.JsonException)
                {
                    throw context.CreateDiagnostic(
                        code: "tosh.runtime.journalctl_json_parse_failed",
                        title: $"Could not parse structured journal output. {exception.Message}",
                        help: "Try running the external `journalctl` command directly if you are using an unsupported output mode.");
                }

                context.CancellationToken.ThrowIfCancellationRequested();
                yield return CommandDisplaySelectionParser.Apply(context.Runtime, selection, entry);
            }
        }
        finally
        {
            await AwaitAndIgnoreAsync(stderrTask);
            await process.WaitForExitAsync(CancellationToken.None);
            context.Runtime.SetLastExitCode(process.ExitCode);
            context.PipelineExitStatusTracker?.Record(process.ExitCode);
        }

        if (process.ExitCode != 0)
        {
            throw context.CreateDiagnostic(
                code: "tosh.runtime.journalctl_command_failed",
                title: "The system 'journalctl' command failed.");
        }
    }

    private static async Task PumpStandardErrorAsync(Process process, TextWriter errorWriter, CancellationToken cancellationToken)
    {
        while (true)
        {
            var line = await process.StandardError.ReadLineAsync(cancellationToken);

            if (line is null)
            {
                break;
            }

            await errorWriter.WriteLineAsync(line);
        }
    }

    private static async Task AwaitAndIgnoreAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (InvalidOperationException)
        {
        }
        catch (OperationCanceledException)
        {
        }
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

    private sealed record StructuredJournalctlRequest(IReadOnlyList<string> ExternalArguments);
}
