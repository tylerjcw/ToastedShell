using System.Diagnostics;

namespace Tosh.Core.Commands;

[CommandCategory("System")]
public sealed class HostnamectlCommand : ShellCommand
{
    public HostnamectlCommand()
        : base("hostnamectl", "Wraps hostnamectl, returning a structured host-status object for supported JSON-backed status queries.", "hostnamectl [status | <other-command ...>]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
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
                ? "The system 'hostnamectl' command failed."
                : result.StandardError.Trim();

            throw context.CreateDiagnostic(
                code: "tosh::runtime::hostnamectl_command_failed",
                title: message);
        }

        if (!string.IsNullOrWhiteSpace(result.StandardError))
        {
            await context.Runtime.Error.WriteLineAsync(result.StandardError.TrimEnd());
        }

        SystemdHostInfo host;

        try
        {
            host = HostnamectlJsonParser.ParseStatus(result.StandardOutput);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.Text.Json.JsonException)
        {
            throw context.CreateDiagnostic(
                code: "tosh::runtime::hostnamectl_json_parse_failed",
                title: $"Could not parse structured 'hostnamectl status' output. {exception.Message}",
                help: "Try running the external `hostnamectl` command directly if you are using an unsupported output mode.");
        }

        yield return CommandDisplaySelectionParser.Apply(context.Runtime, parsedSelection.Selection, host);
    }

    private static string ResolveExecutable(CommandContext context)
    {
        var lookup = ExternalCommandResolver.Resolve(context.Runtime.CurrentDirectory, "hostnamectl");

        return lookup.Status switch
        {
            ExternalCommandLookupStatus.Found when lookup.ResolvedPath is not null => lookup.ResolvedPath,
            _ => throw context.CreateDiagnostic(
                code: "tosh::runtime::hostnamectl_command_missing",
                title: "The system 'hostnamectl' command was not found.",
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
                string.Equals(argument, "--transient", StringComparison.Ordinal) ||
                string.Equals(argument, "--static", StringComparison.Ordinal) ||
                string.Equals(argument, "--pretty", StringComparison.Ordinal)))
        {
            return false;
        }

        if (firstNonOptionIndex >= 0 &&
            !string.Equals(serialized[firstNonOptionIndex], "status", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (HasJsonOffMode(serialized))
        {
            return false;
        }

        var normalized = serialized.ToList();

        if (!ContainsJsonOption(normalized))
        {
            normalized.Add("--json=short");
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

            if (argument is "-H" or "--host" or "-M" or "--machine" or "--json" && index + 1 < arguments.Count)
            {
                index++;
            }
        }

        return -1;
    }

    private static bool ContainsJsonOption(IReadOnlyList<string> arguments)
    {
        return arguments.Any(argument =>
            string.Equals(argument, "-j", StringComparison.Ordinal) ||
            string.Equals(argument, "--json", StringComparison.Ordinal) ||
            argument.StartsWith("--json=", StringComparison.Ordinal));
    }

    private static bool HasJsonOffMode(IReadOnlyList<string> arguments)
    {
        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];

            if (string.Equals(argument, "--json", StringComparison.Ordinal))
            {
                return index + 1 >= arguments.Count ||
                       string.Equals(arguments[index + 1], "off", StringComparison.OrdinalIgnoreCase);
            }

            if (argument.StartsWith("--json=", StringComparison.Ordinal))
            {
                return string.Equals(argument["--json=".Length..], "off", StringComparison.OrdinalIgnoreCase);
            }
        }

        return false;
    }

    private static async Task<HostnamectlProcessResult> ExecuteStructuredAsync(
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
                code: "tosh::runtime::hostnamectl_command_start_failed",
                title: "Failed to start the system 'hostnamectl' command.");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(context.CancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(context.CancellationToken);

        await process.WaitForExitAsync(context.CancellationToken);

        return new HostnamectlProcessResult(
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

    private sealed record HostnamectlProcessResult(
        int ExitCode,
        string StandardOutput,
        string StandardError);
}
