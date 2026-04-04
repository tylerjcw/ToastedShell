using System.Diagnostics;

namespace Tosh.Core.Commands;

public sealed class IpCommand : ShellCommand
{
    public IpCommand()
        : base("ip", "Wraps the system ip utility, returning typed objects for supported JSON-backed subcommands.", "ip [addr|address|a|link|l|route|r [filter ...]] | ip <other-subcommand ...>") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var resolvedPath = ResolveIpExecutable(context);

        if (!TryBuildStructuredArguments(context.Arguments, out var structuredRequest))
        {
            var external = new ExternalProcessCommand(Name, resolvedPath);

            await foreach (var item in external.ExecuteAsync(context))
            {
                yield return item;
            }

            yield break;
        }

        var result = await ExecuteStructuredIpAsync(context, resolvedPath, structuredRequest.Arguments);

        context.Runtime.SetLastExitCode(result.ExitCode);
        context.PipelineExitStatusTracker?.Record(result.ExitCode);

        if (result.ExitCode != 0)
        {
            var message = string.IsNullOrWhiteSpace(result.StandardError)
                ? "The system 'ip' command failed."
                : result.StandardError.Trim();

            throw context.CreateDiagnostic(
                code: "tosh::runtime::ip_command_failed",
                title: message);
        }

        if (!string.IsNullOrWhiteSpace(result.StandardError))
        {
            await context.Runtime.Error.WriteLineAsync(result.StandardError.TrimEnd());
        }

        IReadOnlyList<object?> parsedItems;

        try
        {
            parsedItems = structuredRequest.Mode switch
            {
                StructuredIpMode.Addr or StructuredIpMode.Link =>
                    IpJsonParser.ParseInterfaces(result.StandardOutput).Cast<object?>().ToArray(),
                StructuredIpMode.Route =>
                    IpJsonParser.ParseRoutes(result.StandardOutput).Cast<object?>().ToArray(),
                _ => throw new InvalidOperationException($"Unsupported structured ip mode '{structuredRequest.Mode}'."),
            };
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.Text.Json.JsonException)
        {
            throw context.CreateDiagnostic(
                code: "tosh::runtime::ip_json_parse_failed",
                title: $"Could not parse structured 'ip {structuredRequest.Mode.ToString().ToLowerInvariant()}' output. {exception.Message}",
                help: "Try running the external `ip` command directly if you are using an output mode that does not support JSON.");
        }

        foreach (var item in parsedItems)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            yield return item;
        }
    }

    private static string ResolveIpExecutable(CommandContext context)
    {
        var lookup = ExternalCommandResolver.Resolve(context.Runtime.CurrentDirectory, "ip");

        return lookup.Status switch
        {
            ExternalCommandLookupStatus.Found when lookup.ResolvedPath is not null => lookup.ResolvedPath,
            _ => throw context.CreateDiagnostic(
                code: "tosh::runtime::ip_command_missing",
                title: "The system 'ip' command was not found.",
                help: "Install iproute2 or invoke the external utility by full path once it is available."),
        };
    }

    private static bool TryBuildStructuredArguments(IReadOnlyList<object?> arguments, out StructuredIpRequest structuredRequest)
    {
        structuredRequest = default!;

        if (arguments.Count == 0)
        {
            return false;
        }

        var serializedArguments = arguments
            .Select(ExternalTextSerializer.SerializeArgument)
            .ToArray();

        var subcommandIndex = -1;

        for (var index = 0; index < serializedArguments.Length; index++)
        {
            var argument = serializedArguments[index];

            if (string.IsNullOrWhiteSpace(argument))
            {
                continue;
            }

            if (argument.StartsWith("-", StringComparison.Ordinal))
            {
                continue;
            }

            subcommandIndex = index;
            break;
        }

        if (subcommandIndex < 0)
        {
            return false;
        }

        var subcommand = serializedArguments[subcommandIndex];

        if (!TryNormalizeStructuredSubcommand(subcommand, out var normalizedSubcommand, out var mode))
        {
            return false;
        }

        var normalized = new List<string>();
        var hasJsonFlag = false;

        foreach (var argument in serializedArguments)
        {
            if (string.Equals(argument, "-j", StringComparison.Ordinal) ||
                string.Equals(argument, "--json", StringComparison.Ordinal))
            {
                hasJsonFlag = true;
            }

            normalized.Add(argument);
        }

        normalized[subcommandIndex] = normalizedSubcommand;

        if (!hasJsonFlag)
        {
            normalized.Insert(0, "-j");
        }

        structuredRequest = new StructuredIpRequest(mode, normalized);
        return true;
    }

    private static bool TryNormalizeStructuredSubcommand(
        string subcommand,
        out string normalizedSubcommand,
        out StructuredIpMode mode)
    {
        if (string.Equals(subcommand, "addr", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(subcommand, "address", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(subcommand, "a", StringComparison.OrdinalIgnoreCase))
        {
            normalizedSubcommand = "addr";
            mode = StructuredIpMode.Addr;
            return true;
        }

        if (string.Equals(subcommand, "link", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(subcommand, "l", StringComparison.OrdinalIgnoreCase))
        {
            normalizedSubcommand = "link";
            mode = StructuredIpMode.Link;
            return true;
        }

        if (string.Equals(subcommand, "route", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(subcommand, "r", StringComparison.OrdinalIgnoreCase))
        {
            normalizedSubcommand = "route";
            mode = StructuredIpMode.Route;
            return true;
        }

        normalizedSubcommand = string.Empty;
        mode = default;
        return false;
    }

    private static async Task<IpProcessResult> ExecuteStructuredIpAsync(
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
                code: "tosh::runtime::ip_command_start_failed",
                title: "Failed to start the system 'ip' command.");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(context.CancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(context.CancellationToken);

        await process.WaitForExitAsync(context.CancellationToken);

        return new IpProcessResult(
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

    private enum StructuredIpMode
    {
        Addr,
        Link,
        Route,
    }

    private sealed record StructuredIpRequest(StructuredIpMode Mode, IReadOnlyList<string> Arguments);

    private sealed record IpProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
