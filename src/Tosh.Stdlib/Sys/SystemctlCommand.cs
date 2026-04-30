using System.Diagnostics;

using Tosh.Runtime;

namespace Tosh.Stdlib.Sys;

[CommandCategory("System")]
[CommandArgument("[list-units [pattern ...]]", "With no subcommand, ToSh treats `systemctl` as a structured `list-units` query. Explicit `list-units` behaves the same way.", Required = false)]
[CommandArgument("list-unit-files [pattern ...]", "Returns typed unit-file state rows for installed unit files.", Required = false)]
[CommandArgument("show <unit ...>", "Returns structured systemd unit property sets for one or more units.", Required = false)]
[CommandArgument("status <unit ...>", "Returns structured unit status objects for one or more units, including recent logs when available.", Required = false)]
[CommandArgument("<other-subcommand ...>", "Unsupported subcommands fall back to the native `systemctl` utility unchanged.", Required = false)]
[CommandOption("--type <type[,type...]>|-t <type[,type...]>", "Restrict structured `list-units` or `list-unit-files` output to specific unit types such as `service` or `socket`.")]
[CommandOption("--state <state[,state...]>", "Restrict structured `list-units` output to specific load/active states.")]
[CommandOption("--all", "Include inactive and unloaded units in the structured listing.")]
[CommandOption("--failed", "Restrict the structured listing to failed units.")]
[CommandOption("-p <property[,property...]>|--property <property[,property...]>", "Restrict `show` to specific fetched properties. ToSh still injects `Id` internally so multiple-unit output stays structured.")]
[CommandOption("--show <columns>", "Use ToSh display-only column selection on structured unit rows or property sets.")]
[CommandOption("--hide <columns>", "Hide display columns while preserving the underlying typed objects.")]
[CommandOption("--show-all", "Expose every selectable structured display column for the current output shape.")]
[CommandExample("systemctl", Title = "List units as typed rows")]
[CommandExample("systemctl --type service | where _.Active == active", Title = "Filter active services in the pipeline")]
[CommandExample("systemctl list-unit-files --type service | where _.Enabled", Title = "Inspect installed enabled service unit files")]
[CommandExample("systemctl status sshd.service | get { Id, ActiveState, MainPID, RecentLogCount }", Title = "Inspect structured unit status details")]
[CommandOutput("Returns typed systemd unit rows for `list-units`, typed unit-file rows for `list-unit-files`, and structured unit property-set objects for supported `show` and `status` queries. Other subcommands currently fall back to the native `systemctl` output.")]
[PipelineInput(Description = "The structured `systemctl` builtin is explicit-arg-first and does not currently consume pipeline input.")]
public sealed class SystemctlCommand : ShellCommand
{
    private static readonly IReadOnlySet<string> KnownNonStructuredSubcommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "is-active",
        "is-enabled",
        "is-failed",
        "is-system-running",
        "start",
        "stop",
        "restart",
        "reload",
        "try-restart",
        "reload-or-restart",
        "reload-or-try-restart",
        "kill",
        "reset-failed",
        "enable",
        "disable",
        "reenable",
        "preset",
        "preset-all",
        "mask",
        "unmask",
        "link",
        "revert",
        "set-property",
        "bind",
        "add-wants",
        "add-requires",
        "set-default",
        "get-default",
        "rescue",
        "emergency",
        "halt",
        "poweroff",
        "reboot",
        "kexec",
        "exit",
        "switch-root",
        "suspend",
        "hibernate",
        "hybrid-sleep",
        "suspend-then-hibernate",
        "default",
        "list-timers",
        "list-sockets",
        "cat",
        "edit",
        "daemon-reload",
        "daemon-reexec",
        "show-environment",
        "set-environment",
        "unset-environment",
        "import-environment",
        "clean",
        "freeze",
        "thaw",
        "cancel",
        "list-jobs",
        "list-dependencies",
    };

    public SystemctlCommand()
        : base("systemctl", "Wraps systemctl, returning typed unit rows for supported list queries and structured unit property sets for supported show and status queries.", "systemctl [list-units [pattern ...] | list-unit-files [pattern ...] | show <unit ...> [options] | status <unit ...> | <other-subcommand ...>]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (OperatingSystem.IsWindows())
        {
            throw context.CreateDiagnostic(
                code: "tosh.runtime.command_windows_unavailable",
                title: $"'{Name}' is not available on Windows.",
                help: "This command requires systemd, which is a Linux-only service manager.");
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

        var result = await ExecuteStructuredAsync(context, resolvedPath, request.ExternalArguments);

        context.Runtime.SetLastExitCode(result.ExitCode);
        context.PipelineExitStatusTracker?.Record(result.ExitCode);

        if (result.ExitCode != 0)
        {
            var message = string.IsNullOrWhiteSpace(result.StandardError)
                ? "The system 'systemctl' command failed."
                : result.StandardError.Trim();

            throw context.CreateDiagnostic(
                code: "tosh.runtime.systemctl_command_failed",
                title: message);
        }

        if (!string.IsNullOrWhiteSpace(result.StandardError))
        {
            await context.Runtime.Error.WriteLineAsync(result.StandardError.TrimEnd());
        }

        switch (request.Mode)
        {
            case StructuredSystemctlMode.ListUnits:
                {
                    IReadOnlyList<SystemdUnitInfo> units;

                    try
                    {
                        units = SystemctlJsonParser.ParseUnitList(result.StandardOutput);
                    }
                    catch (Exception exception) when (exception is InvalidOperationException or System.Text.Json.JsonException)
                    {
                        throw context.CreateDiagnostic(
                            code: "tosh.runtime.systemctl_json_parse_failed",
                            title: $"Could not parse structured 'systemctl list-units' output. {exception.Message}",
                            help: "Try running the external `systemctl` command directly if you are using an unsupported output mode.");
                    }

                    foreach (var unit in units)
                    {
                        context.CancellationToken.ThrowIfCancellationRequested();
                        yield return CommandDisplaySelectionParser.Apply(context.Runtime, parsedSelection.Selection, unit);
                    }

                    yield break;
                }
            case StructuredSystemctlMode.ListUnitFiles:
                {
                    IReadOnlyList<SystemdUnitFileInfo> unitFiles;

                    try
                    {
                        unitFiles = SystemctlJsonParser.ParseUnitFileList(result.StandardOutput);
                    }
                    catch (Exception exception) when (exception is InvalidOperationException or System.Text.Json.JsonException)
                    {
                        throw context.CreateDiagnostic(
                            code: "tosh.runtime.systemctl_json_parse_failed",
                            title: $"Could not parse structured 'systemctl list-unit-files' output. {exception.Message}",
                            help: "Try running the external `systemctl` command directly if you are using an unsupported output mode.");
                    }

                    foreach (var unitFile in unitFiles)
                    {
                        context.CancellationToken.ThrowIfCancellationRequested();
                        yield return CommandDisplaySelectionParser.Apply(context.Runtime, parsedSelection.Selection, unitFile);
                    }

                    yield break;
                }
            case StructuredSystemctlMode.Show:
            case StructuredSystemctlMode.Status:
                {
                    IReadOnlyList<SystemdUnitPropertySet> units;

                    try
                    {
                        units = SystemctlJsonParser.ParseShowOutput(result.StandardOutput);
                    }
                    catch (Exception exception) when (exception is InvalidOperationException)
                    {
                        throw context.CreateDiagnostic(
                            code: "tosh.runtime.systemctl_show_parse_failed",
                            title: $"Could not parse structured 'systemctl show' output. {exception.Message}",
                            help: "Try running the external `systemctl show` command directly if you are using an unsupported property/value mode.");
                    }

                    if (request.Mode is StructuredSystemctlMode.Status)
                    {
                        await EnrichStatusResultsAsync(context, units);
                    }

                    foreach (var unit in units)
                    {
                        context.CancellationToken.ThrowIfCancellationRequested();
                        yield return CommandDisplaySelectionParser.Apply(context.Runtime, parsedSelection.Selection, unit);
                    }

                    yield break;
                }
            default:
                throw new InvalidOperationException($"Unexpected structured systemctl mode '{request.Mode}'.");
        }
    }

    private static string ResolveExecutable(CommandContext context)
    {
        var lookup = ExternalCommandResolver.Resolve(context.Runtime.CurrentDirectory, "systemctl");

        return lookup.Status switch
        {
            ExternalCommandLookupStatus.Found when lookup.ResolvedPath is not null => lookup.ResolvedPath,
            _ => throw context.CreateDiagnostic(
                code: "tosh.runtime.systemctl_command_missing",
                title: "The system 'systemctl' command was not found.",
                help: "Install systemd or invoke the external utility by full path once it is available."),
        };
    }

    private static bool TryParseStructuredRequest(
        IReadOnlyList<object?> arguments,
        out StructuredSystemctlRequest request)
    {
        var serialized = arguments
            .Select(ExternalTextSerializer.SerializeArgument)
            .ToArray();

        var firstNonOptionIndex = FindFirstNonOptionIndex(serialized);

        if (firstNonOptionIndex >= 0 &&
            string.Equals(serialized[firstNonOptionIndex], "list-units", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryBuildListUnitsArguments(serialized, firstNonOptionIndex, out var listArguments))
            {
                request = null!;
                return false;
            }

            request = new StructuredSystemctlRequest(StructuredSystemctlMode.ListUnits, listArguments);
            return true;
        }

        if (firstNonOptionIndex >= 0 &&
            string.Equals(serialized[firstNonOptionIndex], "list-unit-files", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryBuildListUnitFilesArguments(serialized, firstNonOptionIndex, out var listArguments))
            {
                request = null!;
                return false;
            }

            request = new StructuredSystemctlRequest(StructuredSystemctlMode.ListUnitFiles, listArguments);
            return true;
        }

        if (firstNonOptionIndex >= 0 &&
            string.Equals(serialized[firstNonOptionIndex], "show", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryBuildShowArguments(serialized, firstNonOptionIndex, out var showArguments))
            {
                request = null!;
                return false;
            }

            request = new StructuredSystemctlRequest(StructuredSystemctlMode.Show, showArguments);
            return true;
        }

        if (firstNonOptionIndex >= 0 &&
            string.Equals(serialized[firstNonOptionIndex], "status", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryBuildStatusArguments(serialized, firstNonOptionIndex, out var statusArguments))
            {
                request = null!;
                return false;
            }

            request = new StructuredSystemctlRequest(StructuredSystemctlMode.Status, statusArguments);
            return true;
        }

        if (firstNonOptionIndex >= 0 &&
            KnownNonStructuredSubcommands.Contains(serialized[firstNonOptionIndex]))
        {
            request = null!;
            return false;
        }

        if (!TryBuildListUnitsArguments(serialized, explicitSubcommandIndex: null, out var defaultArguments))
        {
            request = null!;
            return false;
        }

        request = new StructuredSystemctlRequest(StructuredSystemctlMode.ListUnits, defaultArguments);
        return true;
    }

    private static bool TryBuildListUnitsArguments(
        IReadOnlyList<string> serialized,
        int? explicitSubcommandIndex,
        out IReadOnlyList<string> arguments)
    {
        arguments = Array.Empty<string>();

        if (HasUnsupportedOutputMode(serialized))
        {
            return false;
        }

        var normalized = serialized.ToList();
        var insertIndex = explicitSubcommandIndex ?? Math.Max(0, FindFirstNonOptionIndex(serialized));

        if (explicitSubcommandIndex is null)
        {
            normalized.Insert(insertIndex, "list-units");
        }

        if (!ContainsOption(normalized, "-o", "--output"))
        {
            normalized.Add("--output=json");
        }

        if (!normalized.Any(argument => string.Equals(argument, "--no-pager", StringComparison.Ordinal)))
        {
            normalized.Add("--no-pager");
        }

        arguments = normalized;
        return true;
    }

    private static bool TryBuildListUnitFilesArguments(
        IReadOnlyList<string> serialized,
        int _,
        out IReadOnlyList<string> arguments)
    {
        arguments = Array.Empty<string>();

        if (HasUnsupportedOutputMode(serialized))
        {
            return false;
        }

        var normalized = serialized.ToList();

        if (!ContainsOption(normalized, "-o", "--output"))
        {
            normalized.Add("--output=json");
        }

        if (!normalized.Any(argument => string.Equals(argument, "--no-pager", StringComparison.Ordinal)))
        {
            normalized.Add("--no-pager");
        }

        arguments = normalized;
        return true;
    }

    private static bool TryBuildShowArguments(
        IReadOnlyList<string> serialized,
        int explicitSubcommandIndex,
        out IReadOnlyList<string> arguments)
    {
        arguments = Array.Empty<string>();

        if (serialized.Any(argument =>
                string.Equals(argument, "--value", StringComparison.Ordinal) ||
                string.Equals(argument, "-P", StringComparison.Ordinal) ||
                string.Equals(argument, "--output", StringComparison.Ordinal) ||
                string.Equals(argument, "-o", StringComparison.Ordinal) ||
                argument.StartsWith("--output=", StringComparison.Ordinal)))
        {
            return false;
        }

        var normalized = serialized.ToList();
        var units = new List<string>();
        var hasIdProperty = false;
        var hasPropertyFilter = false;

        for (var index = explicitSubcommandIndex + 1; index < normalized.Count; index++)
        {
            var argument = normalized[index];

            if (string.Equals(argument, "-p", StringComparison.Ordinal) ||
                string.Equals(argument, "--property", StringComparison.Ordinal))
            {
                if (index + 1 >= normalized.Count)
                {
                    return false;
                }

                hasPropertyFilter = true;
                hasIdProperty |= ContainsPropertyName(normalized[index + 1], "Id");
                index++;
                continue;
            }

            if (argument.StartsWith("--property=", StringComparison.Ordinal))
            {
                hasPropertyFilter = true;
                hasIdProperty |= ContainsPropertyName(argument["--property=".Length..], "Id");
                continue;
            }

            if (argument.StartsWith("-", StringComparison.Ordinal))
            {
                if (SystemctlOptionRequiresValue(argument) && index + 1 < normalized.Count)
                {
                    index++;
                }

                continue;
            }

            units.Add(argument);
        }

        if (units.Count == 0)
        {
            return false;
        }

        if (hasPropertyFilter && !hasIdProperty)
        {
            normalized.Add("-p");
            normalized.Add("Id");
        }

        if (!normalized.Any(argument => string.Equals(argument, "--no-pager", StringComparison.Ordinal)))
        {
            normalized.Add("--no-pager");
        }

        arguments = normalized;
        return true;
    }

    private static bool TryBuildStatusArguments(
        IReadOnlyList<string> serialized,
        int explicitSubcommandIndex,
        out IReadOnlyList<string> arguments)
    {
        arguments = Array.Empty<string>();

        var normalized = serialized.ToList();
        var units = new List<string>();

        for (var index = explicitSubcommandIndex + 1; index < normalized.Count; index++)
        {
            var argument = normalized[index];

            if (argument.StartsWith("-", StringComparison.Ordinal))
            {
                if (string.Equals(argument, "--no-pager", StringComparison.Ordinal))
                {
                    continue;
                }

                if (SystemctlStatusOptionRequiresFallback(argument))
                {
                    return false;
                }

                if (SystemctlOptionRequiresValue(argument) && index + 1 < normalized.Count)
                {
                    index++;
                }

                continue;
            }

            units.Add(argument);
        }

        if (units.Count == 0)
        {
            return false;
        }

        var externalArguments = new List<string>();

        for (var index = 0; index < normalized.Count; index++)
        {
            if (index == explicitSubcommandIndex)
            {
                externalArguments.Add("show");
                continue;
            }

            externalArguments.Add(normalized[index]);
        }

        if (!externalArguments.Any(argument => string.Equals(argument, "--no-pager", StringComparison.Ordinal)))
        {
            externalArguments.Add("--no-pager");
        }

        arguments = externalArguments;
        return true;
    }

    private static bool ContainsOption(IReadOnlyList<string> arguments, params string[] names)
    {
        return arguments.Any(argument =>
            names.Any(name => string.Equals(argument, name, StringComparison.Ordinal) ||
                              argument.StartsWith($"{name}=", StringComparison.Ordinal)));
    }

    private static bool HasUnsupportedOutputMode(IReadOnlyList<string> arguments)
    {
        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];

            if (string.Equals(argument, "-o", StringComparison.Ordinal) ||
                string.Equals(argument, "--output", StringComparison.Ordinal))
            {
                if (index + 1 >= arguments.Count)
                {
                    return true;
                }

                return !string.Equals(arguments[index + 1], "json", StringComparison.OrdinalIgnoreCase);
            }

            if (argument.StartsWith("--output=", StringComparison.Ordinal))
            {
                return !string.Equals(argument["--output=".Length..], "json", StringComparison.OrdinalIgnoreCase);
            }
        }

        return false;
    }

    private static bool ContainsPropertyName(string value, string propertyName)
    {
        return value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(item => string.Equals(item, propertyName, StringComparison.OrdinalIgnoreCase));
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

            if (SystemctlOptionRequiresValue(argument) && index + 1 < arguments.Count)
            {
                index++;
            }
        }

        return -1;
    }

    private static bool SystemctlOptionRequiresValue(string argument)
    {
        return argument is "-t" or "--type" or "--state" or "-p" or "--property" or "-H" or "--host" or "-M" or "--machine" ||
               string.Equals(argument, "-n", StringComparison.Ordinal);
    }

    private static bool SystemctlStatusOptionRequiresFallback(string argument)
    {
        return argument is "-l" or "--full" or "-n" or "--lines" or "--quiet" or "--plain" or "--value" ||
               string.Equals(argument, "-P", StringComparison.Ordinal) ||
               string.Equals(argument, "-o", StringComparison.Ordinal) ||
               string.Equals(argument, "--output", StringComparison.Ordinal) ||
               argument.StartsWith("--output=", StringComparison.Ordinal) ||
               argument.StartsWith("--lines=", StringComparison.Ordinal);
    }

    private static async Task<SystemctlProcessResult> ExecuteStructuredAsync(
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
                code: "tosh.runtime.systemctl_command_start_failed",
                title: "Failed to start the system 'systemctl' command.");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(context.CancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(context.CancellationToken);

        await process.WaitForExitAsync(context.CancellationToken);

        return new SystemctlProcessResult(
            process.ExitCode,
            await stdoutTask,
            await stderrTask);
    }

    private static async Task EnrichStatusResultsAsync(
        CommandContext context,
        IReadOnlyList<SystemdUnitPropertySet> units)
    {
        if (units.Count == 0)
        {
            return;
        }

        var journalLookup = ExternalCommandResolver.Resolve(context.Runtime.CurrentDirectory, "journalctl");

        if (journalLookup.Status is not ExternalCommandLookupStatus.Found || journalLookup.ResolvedPath is null)
        {
            return;
        }

        var unitIds = units
            .Select(unit => unit.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (unitIds.Length == 0)
        {
            return;
        }

        try
        {
            var entries = await ReadRecentJournalEntriesAsync(
                context,
                journalLookup.ResolvedPath,
                unitIds,
                maxEntries: 10);

            var entriesByUnit = entries
                .Select(entry => new
                {
                    Key = entry.Unit ?? entry.UserUnit,
                    Entry = entry,
                })
                .Where(item => !string.IsNullOrWhiteSpace(item.Key))
                .GroupBy(item => item.Key!, item => item.Entry, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => (IReadOnlyList<SystemdJournalEntry>)group.ToArray(), StringComparer.OrdinalIgnoreCase);

            foreach (var unit in units)
            {
                var recentLog = unit.Id is not null && entriesByUnit.TryGetValue(unit.Id, out var matchingEntries)
                    ? matchingEntries
                    : Array.Empty<SystemdJournalEntry>();

                unit.TrySetMember("RecentLog", recentLog);
            }
        }
        catch
        {
            // Keep `systemctl status` useful even if journal access is unavailable.
        }
    }

    private static async Task<IReadOnlyList<SystemdJournalEntry>> ReadRecentJournalEntriesAsync(
        CommandContext context,
        string resolvedPath,
        IReadOnlyList<string> unitIds,
        int maxEntries)
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

        startInfo.ArgumentList.Add("--no-pager");
        startInfo.ArgumentList.Add("-o");
        startInfo.ArgumentList.Add("json");
        startInfo.ArgumentList.Add("-n");
        startInfo.ArgumentList.Add(maxEntries.ToString(System.Globalization.CultureInfo.InvariantCulture));

        foreach (var unitId in unitIds)
        {
            startInfo.ArgumentList.Add("-u");
            startInfo.ArgumentList.Add(unitId);
        }

        using var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true,
        };
        using var cancellationRegistration = context.CancellationToken.Register(() => TryKill(process));

        if (!process.Start())
        {
            return Array.Empty<SystemdJournalEntry>();
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(context.CancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(context.CancellationToken);

        await process.WaitForExitAsync(context.CancellationToken);

        _ = await stderrTask;

        if (process.ExitCode != 0)
        {
            return Array.Empty<SystemdJournalEntry>();
        }

        return JournalctlJsonParser.ParseMany(await stdoutTask);
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

    private enum StructuredSystemctlMode
    {
        ListUnits,
        ListUnitFiles,
        Show,
        Status,
    }

    private sealed record StructuredSystemctlRequest(
        StructuredSystemctlMode Mode,
        IReadOnlyList<string> ExternalArguments);

    private sealed record SystemctlProcessResult(
        int ExitCode,
        string StandardOutput,
        string StandardError);
}
