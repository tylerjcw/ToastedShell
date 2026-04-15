using System.Diagnostics;

namespace Tosh.Core.Commands;

[CommandCategory("Network")]
[CommandArgument("addr|address|a [filter ...]", "Returns typed network-interface objects by invoking `ip -j addr` under the hood.", Required = false)]
[CommandArgument("link|l [filter ...]", "Returns typed network-interface link objects by invoking `ip -j link`.", Required = false)]
[CommandArgument("route|r [filter ...]", "Returns typed route objects by invoking `ip -j route`.", Required = false)]
[CommandArgument("neigh|neighbour|n [filter ...]", "Returns typed ARP/neighbor table objects by invoking `ip -j neigh`.", Required = false)]
[CommandArgument("rule|ru [filter ...]", "Returns typed routing policy rule objects by invoking `ip -j rule`.", Required = false)]
[CommandArgument("<other-subcommand ...>", "For now, unsupported subcommands fall back to the system `ip` utility unchanged.", Required = false)]
[CommandExample("ip addr", Title = "List interfaces as structured objects")]
[CommandExample("ip link | where _.IsUp", Title = "Filter active interfaces in the pipeline")]
[CommandExample("ip route | where _.IsDefault", Title = "Show the default route as a typed object")]
[CommandExample("ip addr | each { _.Addresses } | flatten | get Address", Title = "Project nested typed IP addresses")]
[CommandNote("ToSh wraps `ip addr`, `ip link`, `ip route`, `ip neigh`, and `ip rule` around the JSON-capable system utility so the result flows through the pipeline as typed objects. Other subcommands still fall back to the external `ip` utility unchanged.")]
[CommandOutput("For `ip addr` and `ip link`, returns typed interface objects with nested typed address objects where available. For `ip route`, returns typed route objects. For `ip neigh`, returns typed neighbor/ARP entries. For `ip rule`, returns typed routing policy rules. Other subcommands pass through to the system `ip` utility's normal text output.")]
[PipelineInput(Description = "The structured `ip` builtin is explicit-arg-first and does not currently consume pipeline input.")]
public sealed class IpCommand : ShellCommand
{
    public IpCommand()
        : base("ip", "Wraps the system ip utility, returning typed objects for supported JSON-backed subcommands.", "ip [addr|address|a|link|l|route|r [filter ...]] | ip <other-subcommand ...>") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        // On Windows there is no system 'ip' utility; use .NET NetworkInformation APIs instead.
        if (OperatingSystem.IsWindows())
        {
            if (!TryBuildStructuredArguments(context.Arguments, out var windowsRequest))
            {
                throw context.CreateDiagnostic(
                    code: "tosh::runtime::ip_command_missing",
                    title: "The system 'ip' command is not available on Windows.",
                    help: "Only 'ip addr', 'ip link', and 'ip route' are supported on Windows.");
            }

            IReadOnlyList<object?> windowsItems = windowsRequest.Mode switch
            {
                StructuredIpMode.Addr =>
                    NetworkInformationServices.GetWindowsInterfaces(includeAddresses: true).Cast<object?>().ToArray(),
                StructuredIpMode.Link =>
                    NetworkInformationServices.GetWindowsInterfaces(includeAddresses: false).Cast<object?>().ToArray(),
                StructuredIpMode.Route =>
                    NetworkInformationServices.GetWindowsRoutes().Cast<object?>().ToArray(),
                _ => throw context.CreateDiagnostic(
                    code: "tosh::runtime::ip_subcommand_unsupported_windows",
                    title: $"'ip {windowsRequest.Mode.ToString().ToLowerInvariant()}' is not supported on Windows.",
                    help: "Only 'ip addr', 'ip link', and 'ip route' are supported on Windows."),
            };

            foreach (var item in windowsItems)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                yield return item;
            }

            yield break;
        }

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
                StructuredIpMode.Neigh =>
                    IpJsonParser.ParseNeighbors(result.StandardOutput).Cast<object?>().ToArray(),
                StructuredIpMode.Rule =>
                    IpJsonParser.ParseRules(result.StandardOutput).Cast<object?>().ToArray(),
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

        if (string.Equals(subcommand, "neigh", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(subcommand, "neighbour", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(subcommand, "neighbor", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(subcommand, "n", StringComparison.OrdinalIgnoreCase))
        {
            normalizedSubcommand = "neigh";
            mode = StructuredIpMode.Neigh;
            return true;
        }

        if (string.Equals(subcommand, "rule", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(subcommand, "ru", StringComparison.OrdinalIgnoreCase))
        {
            normalizedSubcommand = "rule";
            mode = StructuredIpMode.Rule;
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
        Neigh,
        Rule,
    }

    private sealed record StructuredIpRequest(StructuredIpMode Mode, IReadOnlyList<string> Arguments);

    private sealed record IpProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
