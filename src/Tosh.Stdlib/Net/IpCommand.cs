using System.Diagnostics;

using Tosh.Runtime;

namespace Tosh.Stdlib.Net;

[CommandCategory("Network")]
[CommandArgument("addr|address|a [show|add|del|flush] [filter ...]", "Query or mutate network addresses. Queries return typed objects; mutations pass through to the system `ip`.", Required = false)]
[CommandArgument("link|l [show|set] [filter ...]", "Query or mutate network links. Queries return typed objects; mutations (set up/down, etc.) pass through.", Required = false)]
[CommandArgument("route|r [show|add|del|change|replace] [filter ...]", "Query or mutate routing table entries. Queries return typed objects; mutations pass through.", Required = false)]
[CommandArgument("neigh|neighbour|n [show|add|del|flush] [filter ...]", "Query or mutate ARP/neighbor table. Queries return typed objects; mutations pass through.", Required = false)]
[CommandArgument("rule|ru [show|add|del] [filter ...]", "Query or mutate routing policy rules. Queries return typed objects; mutations pass through.", Required = false)]
[CommandArgument("netns [list|add|del|exec ...]", "Query or manage network namespaces. `list` returns typed objects; other operations pass through.", Required = false)]
[CommandArgument("tunnel|tun [show|add|del|change] [filter ...]", "Query or mutate IP tunnels. Queries return typed objects; mutations pass through.", Required = false)]
[CommandArgument("tuntap|tap [show|add|del] [filter ...]", "Query or mutate TUN/TAP devices. Queries return typed objects; mutations pass through.", Required = false)]
[CommandArgument("vrf [show|exec ...] [filter ...]", "Query VRF devices or execute commands in a VRF context. `show` returns typed objects; `exec` passes through.", Required = false)]
[CommandArgument("maddr|maddress [show] [filter ...]", "Query multicast addresses per interface. Returns typed objects.", Required = false)]
[CommandArgument("mroute|mr [show] [filter ...]", "Query multicast routing table entries. Returns typed objects.", Required = false)]
[CommandArgument("token [list|set|del] [filter ...]", "Query or mutate tokenized interface identifiers. `list` returns typed objects; mutations pass through.", Required = false)]
[CommandArgument("ntable [show|change] [filter ...]", "Query or mutate the neighbor table parameters. `show` returns typed objects; mutations pass through.", Required = false)]
[CommandArgument("<other-subcommand ...>", "Unsupported subcommands fall back to the system `ip` utility unchanged.", Required = false)]
[CommandExample("ip addr", Title = "List interfaces as structured objects")]
[CommandExample("ip link | where _.IsUp", Title = "Filter active interfaces in the pipeline")]
[CommandExample("ip route | where _.IsDefault", Title = "Show the default route as a typed object")]
[CommandExample("ip addr | each { _.Addresses } | flatten | get Address", Title = "Project nested typed IP addresses")]
[CommandExample("ip addr add 192.168.1.100/24 dev eth0", Title = "Add an IP address to an interface")]
[CommandExample("ip link set eth0 up", Title = "Bring an interface up")]
[CommandExample("ip route del default", Title = "Delete the default route")]
[CommandExample("ip netns", Title = "List network namespaces as structured objects")]
[CommandExample("ip tunnel", Title = "List IP tunnels as structured objects")]
[CommandExample("ip tuntap", Title = "List TUN/TAP devices as structured objects")]
[CommandExample("ip maddr", Title = "List multicast addresses per interface")]
[CommandExample("ip ntable", Title = "Show neighbor table parameters")]
[CommandNote("ToSh wraps `ip addr`, `ip link`, `ip route`, `ip neigh`, `ip rule`, `ip netns`, `ip tunnel`, `ip tuntap`, `ip vrf`, `ip maddr`, `ip mroute`, `ip token`, and `ip ntable` around the JSON-capable system utility so queries flow through the pipeline as typed objects. Mutation verbs (add, del, set, change, replace, flush) pass through to the system `ip` utility directly. Other subcommands fall back to the external `ip` utility unchanged.")]
[CommandOutput("Query subcommands return typed objects (interfaces, routes, neighbors, rules, namespaces). Mutation subcommands (add, del, set, etc.) pass through to the system `ip` utility and return its text output.")]
[PipelineInput(Description = "The structured `ip` builtin is explicit-arg-first and does not currently consume pipeline input.")]
public sealed class IpCommand : ShellCommand
{
    public IpCommand()
        : base("ip", "Wraps the system ip utility, returning typed objects for supported JSON-backed subcommands.", "ip [addr|link|route|neigh|rule|netns|tunnel|tuntap|vrf|maddr|mroute|token|ntable] [verb] [filter ...] | ip <other-subcommand ...>") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        // On Windows there is no system 'ip' utility; use .NET NetworkInformation APIs instead.
        if (OperatingSystem.IsWindows())
        {
            if (!TryBuildStructuredArguments(context.Arguments, out var windowsRequest))
            {
                throw context.CreateDiagnostic(
                    code: "tosh.runtime.ip_command_missing",
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
                    code: "tosh.runtime.ip_subcommand_unsupported_windows",
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

        context.Shell().SetLastExitCode(result.ExitCode);
        context.PipelineExitStatusTracker?.Record(result.ExitCode);

        if (result.ExitCode != 0)
        {
            var message = string.IsNullOrWhiteSpace(result.StandardError)
                ? "The system 'ip' command failed."
                : result.StandardError.Trim();

            throw context.CreateDiagnostic(
                code: "tosh.runtime.ip_command_failed",
                title: message);
        }

        if (!string.IsNullOrWhiteSpace(result.StandardError))
        {
            await context.Shell().Error.WriteLineAsync(result.StandardError.TrimEnd());
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
                StructuredIpMode.Netns =>
                    IpJsonParser.ParseNamespaces(result.StandardOutput).Cast<object?>().ToArray(),
                StructuredIpMode.Tunnel =>
                    IpJsonParser.ParseTunnels(result.StandardOutput).Cast<object?>().ToArray(),
                StructuredIpMode.Tuntap =>
                    IpJsonParser.ParseTuntaps(result.StandardOutput).Cast<object?>().ToArray(),
                StructuredIpMode.Vrf =>
                    IpJsonParser.ParseVrfs(result.StandardOutput).Cast<object?>().ToArray(),
                StructuredIpMode.Maddr =>
                    IpJsonParser.ParseMaddrs(result.StandardOutput).Cast<object?>().ToArray(),
                StructuredIpMode.Mroute =>
                    IpJsonParser.ParseMroutes(result.StandardOutput).Cast<object?>().ToArray(),
                StructuredIpMode.Token =>
                    IpJsonParser.ParseTokens(result.StandardOutput).Cast<object?>().ToArray(),
                StructuredIpMode.Ntable =>
                    IpJsonParser.ParseNtables(result.StandardOutput).Cast<object?>().ToArray(),
                _ => throw new InvalidOperationException($"Unsupported structured ip mode '{structuredRequest.Mode}'."),
            };
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.Text.Json.JsonException)
        {
            throw context.CreateDiagnostic(
                code: "tosh.runtime.ip_json_parse_failed",
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
        var lookup = ExternalCommandResolver.Resolve(context.Shell().CurrentDirectory, "ip");

        return lookup.Status switch
        {
            ExternalCommandLookupStatus.Found when lookup.ResolvedPath is not null => lookup.ResolvedPath,
            _ => throw context.CreateDiagnostic(
                code: "tosh.runtime.ip_command_missing",
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

        if (HasMutationOrPassthroughVerb(serializedArguments, subcommandIndex, mode))
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

        if (string.Equals(subcommand, "netns", StringComparison.OrdinalIgnoreCase))
        {
            normalizedSubcommand = "netns";
            mode = StructuredIpMode.Netns;
            return true;
        }

        if (string.Equals(subcommand, "tunnel", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(subcommand, "tun", StringComparison.OrdinalIgnoreCase))
        {
            normalizedSubcommand = "tunnel";
            mode = StructuredIpMode.Tunnel;
            return true;
        }

        if (string.Equals(subcommand, "tuntap", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(subcommand, "tap", StringComparison.OrdinalIgnoreCase))
        {
            normalizedSubcommand = "tuntap";
            mode = StructuredIpMode.Tuntap;
            return true;
        }

        if (string.Equals(subcommand, "vrf", StringComparison.OrdinalIgnoreCase))
        {
            normalizedSubcommand = "vrf";
            mode = StructuredIpMode.Vrf;
            return true;
        }

        if (string.Equals(subcommand, "maddr", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(subcommand, "maddress", StringComparison.OrdinalIgnoreCase))
        {
            normalizedSubcommand = "maddr";
            mode = StructuredIpMode.Maddr;
            return true;
        }

        if (string.Equals(subcommand, "mroute", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(subcommand, "mr", StringComparison.OrdinalIgnoreCase))
        {
            normalizedSubcommand = "mroute";
            mode = StructuredIpMode.Mroute;
            return true;
        }

        if (string.Equals(subcommand, "token", StringComparison.OrdinalIgnoreCase))
        {
            normalizedSubcommand = "token";
            mode = StructuredIpMode.Token;
            return true;
        }

        if (string.Equals(subcommand, "ntable", StringComparison.OrdinalIgnoreCase))
        {
            normalizedSubcommand = "ntable";
            mode = StructuredIpMode.Ntable;
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
            WorkingDirectory = context.Shell().CurrentDirectory,
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
                code: "tosh.runtime.ip_command_start_failed",
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

    private static bool HasMutationOrPassthroughVerb(string[] arguments, int subcommandIndex, StructuredIpMode mode)
    {
        var verbIndex = subcommandIndex + 1;

        while (verbIndex < arguments.Length)
        {
            if (!arguments[verbIndex].StartsWith("-", StringComparison.Ordinal))
            {
                break;
            }

            verbIndex++;
        }

        if (verbIndex >= arguments.Length)
        {
            return false;
        }

        var verb = arguments[verbIndex];

        if (mode == StructuredIpMode.Netns)
        {
            return !string.Equals(verb, "list", StringComparison.OrdinalIgnoreCase);
        }

        if (mode == StructuredIpMode.Token)
        {
            return !string.Equals(verb, "list", StringComparison.OrdinalIgnoreCase);
        }

        if (mode == StructuredIpMode.Vrf)
        {
            return string.Equals(verb, "exec", StringComparison.OrdinalIgnoreCase);
        }

        return IsMutationVerb(verb);
    }

    private static bool IsMutationVerb(string verb) =>
        string.Equals(verb, "add", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(verb, "del", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(verb, "delete", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(verb, "change", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(verb, "replace", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(verb, "set", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(verb, "flush", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(verb, "save", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(verb, "restore", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(verb, "append", StringComparison.OrdinalIgnoreCase);

    private enum StructuredIpMode
    {
        Addr,
        Link,
        Route,
        Neigh,
        Rule,
        Netns,
        Tunnel,
        Tuntap,
        Vrf,
        Maddr,
        Mroute,
        Token,
        Ntable,
    }

    private sealed record StructuredIpRequest(StructuredIpMode Mode, IReadOnlyList<string> Arguments);

    private sealed record IpProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
