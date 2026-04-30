using System.Diagnostics;

using Tosh.Runtime;

namespace Tosh.Stdlib.Sys;

[CommandCategory("System")]
[CommandArgument("[list [pattern ...]]", "With no subcommand, ToSh treats `networkctl` as a structured `list` query. Explicit `list` behaves the same way.", Required = false)]
[CommandArgument("<other-command ...>", "Unsupported, detail, and mutating commands currently fall back to the native `networkctl` utility unchanged.", Required = false)]
[CommandOption("-a|--all", "Pass through to the structured `networkctl list` query to include all visible links.")]
[CommandOption("-l|--full", "Pass through to the structured `networkctl list` query.")]
[CommandOption("--show <columns>", "Use ToSh display-only column selection on the structured network-link rows.")]
[CommandOption("--hide <columns>", "Hide display columns while preserving the underlying typed link objects.")]
[CommandOption("--show-all", "Expose every selectable structured network-link display column.")]
[CommandExample("networkctl", Title = "List network links as typed rows")]
[CommandExample("networkctl | where _.Setup == unmanaged", Title = "Filter links by setup state in the pipeline")]
[CommandExample("networkctl --show Link,Operational,Setup,Managed", Title = "Render a focused network-link summary")]
[CommandOutput("Returns typed network-link rows for supported `networkctl list` queries. Other commands currently fall back to the native `networkctl` output.")]
[PipelineInput(Description = "The structured `networkctl` builtin is explicit-arg-first and does not currently consume pipeline input.")]
public sealed class NetworkctlCommand : ShellCommand
{
    private static readonly IReadOnlySet<string> KnownNonStructuredSubcommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "status",
        "lldp",
        "label",
        "delete",
        "up",
        "down",
        "renew",
        "forcerenew",
        "reconfigure",
        "reload",
        "edit",
        "cat",
        "mask",
        "unmask",
        "persistent-storage",
    };

    public NetworkctlCommand()
        : base("networkctl", "Wraps networkctl, returning typed network-link rows for supported list queries.", "networkctl [list [pattern ...] | <other-command ...>]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (OperatingSystem.IsWindows())
        {
            throw context.CreateDiagnostic(
                code: "tosh.runtime.command_windows_unavailable",
                title: $"'{Name}' is not available on Windows.",
                help: "This command requires systemd-networkd, which is a Linux-only service.");
        }

        var resolvedPath = ResolveExecutable(context);
        var parsedSelection = CommandDisplaySelectionParser.Parse(context.Arguments);

        if (!TryParseStructuredRequest(parsedSelection.RemainingArguments, out var externalArguments))
        {
            var external = new ExternalProcessCommand(Name, resolvedPath);

            await foreach (var item in external.ExecuteAsync(context))
            {
                yield return item;
            }

            yield break;
        }

        var result = await ExecuteStructuredAsync(context, resolvedPath, externalArguments);

        context.Runtime.SetLastExitCode(result.ExitCode);
        context.PipelineExitStatusTracker?.Record(result.ExitCode);

        if (result.ExitCode != 0)
        {
            var message = string.IsNullOrWhiteSpace(result.StandardError)
                ? "The system 'networkctl' command failed."
                : result.StandardError.Trim();

            throw context.CreateDiagnostic(
                code: "tosh.runtime.networkctl_command_failed",
                title: message);
        }

        if (!string.IsNullOrWhiteSpace(result.StandardError))
        {
            await context.Runtime.Error.WriteLineAsync(result.StandardError.TrimEnd());
        }

        IReadOnlyList<SystemdNetworkLinkInfo> links;

        try
        {
            links = NetworkctlListParser.Parse(result.StandardOutput);
        }
        catch (Exception exception) when (exception is InvalidOperationException or FormatException)
        {
            throw context.CreateDiagnostic(
                code: "tosh.runtime.networkctl_parse_failed",
                title: $"Could not parse structured 'networkctl list' output. {exception.Message}",
                help: "Try running the external `networkctl` command directly if you are using an unsupported mode.");
        }

        foreach (var link in links)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            yield return CommandDisplaySelectionParser.Apply(context.Runtime, parsedSelection.Selection, link);
        }
    }

    private static string ResolveExecutable(CommandContext context)
    {
        var lookup = ExternalCommandResolver.Resolve(context.Runtime.CurrentDirectory, "networkctl");

        return lookup.Status switch
        {
            ExternalCommandLookupStatus.Found when lookup.ResolvedPath is not null => lookup.ResolvedPath,
            _ => throw context.CreateDiagnostic(
                code: "tosh.runtime.networkctl_command_missing",
                title: "The system 'networkctl' command was not found.",
                help: "Install systemd or invoke the external utility by full path once it is available."),
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

        var firstNonOptionIndex = FindFirstNonOptionIndex(serialized);

        if (serialized.Any(argument =>
                string.Equals(argument, "--json", StringComparison.Ordinal) ||
                argument.StartsWith("--json=", StringComparison.Ordinal) ||
                string.Equals(argument, "-j", StringComparison.Ordinal) ||
                string.Equals(argument, "-s", StringComparison.Ordinal) ||
                string.Equals(argument, "--stats", StringComparison.Ordinal) ||
                string.Equals(argument, "-n", StringComparison.Ordinal) ||
                string.Equals(argument, "--lines", StringComparison.Ordinal) ||
                argument.StartsWith("--lines=", StringComparison.Ordinal)))
        {
            return false;
        }

        if (firstNonOptionIndex >= 0 &&
            KnownNonStructuredSubcommands.Contains(serialized[firstNonOptionIndex]))
        {
            return false;
        }

        var normalized = serialized.ToList();
        var insertIndex = firstNonOptionIndex >= 0 ? firstNonOptionIndex : 0;

        if (firstNonOptionIndex < 0)
        {
            normalized.Insert(0, "list");
        }
        else if (!string.Equals(serialized[firstNonOptionIndex], "list", StringComparison.OrdinalIgnoreCase))
        {
            normalized.Insert(insertIndex, "list");
        }

        for (var index = 0; index < normalized.Count; index++)
        {
            var argument = normalized[index];

            if (index == insertIndex)
            {
                continue;
            }

            if (!argument.StartsWith("-", StringComparison.Ordinal))
            {
                continue;
            }

            if (RequiresValue(argument) && index + 1 < normalized.Count)
            {
                index++;
            }
        }

        if (!normalized.Any(argument => string.Equals(argument, "--no-pager", StringComparison.Ordinal)))
        {
            normalized.Add("--no-pager");
        }

        if (!normalized.Any(argument => string.Equals(argument, "--no-legend", StringComparison.Ordinal)))
        {
            normalized.Add("--no-legend");
        }

        externalArguments = normalized;
        return true;
    }

    private static int FindFirstNonOptionIndex(IReadOnlyList<string> arguments)
    {
        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];

            if (string.IsNullOrWhiteSpace(argument))
            {
                continue;
            }

            if (!argument.StartsWith("-", StringComparison.Ordinal))
            {
                return index;
            }

            if (RequiresValue(argument) && index + 1 < arguments.Count)
            {
                index++;
            }
        }

        return -1;
    }

    private static bool RequiresValue(string argument)
    {
        return argument is "-H" or "--host" or "-M" or "--machine" or "--drop-in";
    }

    private static async Task<NetworkctlProcessResult> ExecuteStructuredAsync(
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
                code: "tosh.runtime.networkctl_command_start_failed",
                title: "Failed to start the system 'networkctl' command.");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(context.CancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(context.CancellationToken);

        await process.WaitForExitAsync(context.CancellationToken);

        return new NetworkctlProcessResult(
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

    private sealed record NetworkctlProcessResult(
        int ExitCode,
        string StandardOutput,
        string StandardError);
}
