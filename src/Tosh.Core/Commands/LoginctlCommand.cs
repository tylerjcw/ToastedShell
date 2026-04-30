using System.Diagnostics;

namespace Tosh.Core.Commands;

[Stdlib(StdlibCategory.System)]
[CommandCategory("System")]
[CommandArgument("[list-sessions|list-users|list-seats]", "With no subcommand, ToSh treats `loginctl` as a structured `list-sessions` query. The explicit list subcommands return typed rows.", Required = false)]
[CommandArgument("show-session <id ...>|show-user <user ...>|show-seat <seat ...>", "Returns structured login property sets for supported `show-*` queries.", Required = false)]
[CommandArgument("<other-subcommand ...>", "Unsupported subcommands fall back to the native `loginctl` utility unchanged.", Required = false)]
[CommandOption("-p <property[,property...]>|--property <property[,property...]>", "Restrict `show-*` to specific fetched properties. ToSh still injects the relevant identity property internally so multiple-result output stays structured.")]
[CommandOption("--all", "Include empty properties in supported `show-*` queries when the underlying `loginctl` invocation supports it.")]
[CommandOption("--show <columns>", "Use ToSh display-only column selection on structured list rows or property sets.")]
[CommandOption("--hide <columns>", "Hide display columns while preserving the underlying typed objects.")]
[CommandOption("--show-all", "Expose every selectable structured display column for the current output shape.")]
[CommandExample("loginctl", Title = "List sessions as typed rows")]
[CommandExample("loginctl list-users | where _.State == active", Title = "Filter active login users in the pipeline")]
[CommandExample("loginctl show-user 1000 | get { UID, Name, State, Sessions }", Title = "Inspect structured user-login properties")]
[CommandOutput("Returns typed session, user, or seat rows for supported list queries, and structured property-set objects for supported `show-*` queries. Other subcommands currently fall back to the native `loginctl` output.")]
[PipelineInput(Description = "The structured `loginctl` builtin is explicit-arg-first and does not currently consume pipeline input.")]
public sealed class LoginctlCommand : ShellCommand
{
    private static readonly IReadOnlySet<string> KnownNonStructuredSubcommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "session-status",
        "user-status",
        "seat-status",
        "activate",
        "lock-session",
        "unlock-session",
        "lock-sessions",
        "unlock-sessions",
        "terminate-session",
        "kill-session",
        "enable-linger",
        "disable-linger",
        "terminate-user",
        "kill-user",
        "attach",
        "flush-devices",
        "terminate-seat",
    };

    public LoginctlCommand()
        : base("loginctl", "Wraps loginctl, returning typed session/user/seat rows and structured login property sets for supported queries.", "loginctl [list-sessions | list-users | list-seats | show-session <id ...> | show-user <user ...> | show-seat <seat ...> | <other-subcommand ...>]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (OperatingSystem.IsWindows())
        {
            throw context.CreateDiagnostic(
                code: "tosh.runtime.command_windows_unavailable",
                title: $"'{Name}' is not available on Windows.",
                help: "This command requires systemd-logind, which is a Linux-only service.");
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
                ? "The system 'loginctl' command failed."
                : result.StandardError.Trim();

            throw context.CreateDiagnostic(
                code: "tosh.runtime.loginctl_command_failed",
                title: message);
        }

        if (!string.IsNullOrWhiteSpace(result.StandardError))
        {
            await context.Runtime.Error.WriteLineAsync(result.StandardError.TrimEnd());
        }

        switch (request.Mode)
        {
            case StructuredLoginctlMode.ListSessions:
                {
                    IReadOnlyList<SystemdLoginSessionInfo> sessions;

                    try
                    {
                        sessions = LoginctlJsonParser.ParseSessionList(result.StandardOutput);
                    }
                    catch (Exception exception) when (exception is InvalidOperationException or System.Text.Json.JsonException)
                    {
                        throw context.CreateDiagnostic(
                            code: "tosh.runtime.loginctl_json_parse_failed",
                            title: $"Could not parse structured 'loginctl list-sessions' output. {exception.Message}",
                            help: "Try running the external `loginctl` command directly if you are using an unsupported output mode.");
                    }

                    foreach (var session in sessions)
                    {
                        context.CancellationToken.ThrowIfCancellationRequested();
                        yield return CommandDisplaySelectionParser.Apply(context.Runtime, parsedSelection.Selection, session);
                    }

                    yield break;
                }
            case StructuredLoginctlMode.ListUsers:
                {
                    IReadOnlyList<SystemdLoginUserInfo> users;

                    try
                    {
                        users = LoginctlJsonParser.ParseUserList(result.StandardOutput);
                    }
                    catch (Exception exception) when (exception is InvalidOperationException or System.Text.Json.JsonException)
                    {
                        throw context.CreateDiagnostic(
                            code: "tosh.runtime.loginctl_json_parse_failed",
                            title: $"Could not parse structured 'loginctl list-users' output. {exception.Message}",
                            help: "Try running the external `loginctl` command directly if you are using an unsupported output mode.");
                    }

                    foreach (var user in users)
                    {
                        context.CancellationToken.ThrowIfCancellationRequested();
                        yield return CommandDisplaySelectionParser.Apply(context.Runtime, parsedSelection.Selection, user);
                    }

                    yield break;
                }
            case StructuredLoginctlMode.ListSeats:
                {
                    IReadOnlyList<SystemdLoginSeatInfo> seats;

                    try
                    {
                        seats = LoginctlJsonParser.ParseSeatList(result.StandardOutput);
                    }
                    catch (Exception exception) when (exception is InvalidOperationException or System.Text.Json.JsonException)
                    {
                        throw context.CreateDiagnostic(
                            code: "tosh.runtime.loginctl_json_parse_failed",
                            title: $"Could not parse structured 'loginctl list-seats' output. {exception.Message}",
                            help: "Try running the external `loginctl` command directly if you are using an unsupported output mode.");
                    }

                    foreach (var seat in seats)
                    {
                        context.CancellationToken.ThrowIfCancellationRequested();
                        yield return CommandDisplaySelectionParser.Apply(context.Runtime, parsedSelection.Selection, seat);
                    }

                    yield break;
                }
            case StructuredLoginctlMode.Show:
                {
                    IReadOnlyList<SystemdPropertySet> rows;

                    try
                    {
                        rows = LoginctlJsonParser.ParseShowOutput(result.StandardOutput, request.IdentityPropertyName!);
                    }
                    catch (Exception exception) when (exception is InvalidOperationException)
                    {
                        throw context.CreateDiagnostic(
                            code: "tosh.runtime.loginctl_show_parse_failed",
                            title: $"Could not parse structured 'loginctl show' output. {exception.Message}",
                            help: "Try running the external `loginctl show-*` command directly if you are using an unsupported property/value mode.");
                    }

                    foreach (var row in rows)
                    {
                        context.CancellationToken.ThrowIfCancellationRequested();
                        yield return CommandDisplaySelectionParser.Apply(context.Runtime, parsedSelection.Selection, row);
                    }

                    yield break;
                }
            default:
                throw new InvalidOperationException($"Unexpected structured loginctl mode '{request.Mode}'.");
        }
    }

    private static string ResolveExecutable(CommandContext context)
    {
        var lookup = ExternalCommandResolver.Resolve(context.Runtime.CurrentDirectory, "loginctl");

        return lookup.Status switch
        {
            ExternalCommandLookupStatus.Found when lookup.ResolvedPath is not null => lookup.ResolvedPath,
            _ => throw context.CreateDiagnostic(
                code: "tosh.runtime.loginctl_command_missing",
                title: "The system 'loginctl' command was not found.",
                help: "Install systemd or invoke the external utility by full path once it is available."),
        };
    }

    private static bool TryParseStructuredRequest(
        IReadOnlyList<object?> arguments,
        out StructuredLoginctlRequest request)
    {
        var serialized = arguments
            .Select(ExternalTextSerializer.SerializeArgument)
            .ToArray();

        var firstNonOptionIndex = FindFirstNonOptionIndex(serialized);

        if (firstNonOptionIndex >= 0 &&
            string.Equals(serialized[firstNonOptionIndex], "list-sessions", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryBuildListArguments(serialized, firstNonOptionIndex, "list-sessions", out var listArguments))
            {
                request = null!;
                return false;
            }

            request = new StructuredLoginctlRequest(StructuredLoginctlMode.ListSessions, listArguments, null);
            return true;
        }

        if (firstNonOptionIndex >= 0 &&
            string.Equals(serialized[firstNonOptionIndex], "list-users", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryBuildListArguments(serialized, firstNonOptionIndex, "list-users", out var listArguments))
            {
                request = null!;
                return false;
            }

            request = new StructuredLoginctlRequest(StructuredLoginctlMode.ListUsers, listArguments, null);
            return true;
        }

        if (firstNonOptionIndex >= 0 &&
            string.Equals(serialized[firstNonOptionIndex], "list-seats", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryBuildListArguments(serialized, firstNonOptionIndex, "list-seats", out var listArguments))
            {
                request = null!;
                return false;
            }

            request = new StructuredLoginctlRequest(StructuredLoginctlMode.ListSeats, listArguments, null);
            return true;
        }

        if (firstNonOptionIndex >= 0 &&
            string.Equals(serialized[firstNonOptionIndex], "show-session", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryBuildShowArguments(serialized, firstNonOptionIndex, "Id", out var showArguments))
            {
                request = null!;
                return false;
            }

            request = new StructuredLoginctlRequest(StructuredLoginctlMode.Show, showArguments, "Id");
            return true;
        }

        if (firstNonOptionIndex >= 0 &&
            string.Equals(serialized[firstNonOptionIndex], "show-user", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryBuildShowArguments(serialized, firstNonOptionIndex, "UID", out var showArguments))
            {
                request = null!;
                return false;
            }

            request = new StructuredLoginctlRequest(StructuredLoginctlMode.Show, showArguments, "UID");
            return true;
        }

        if (firstNonOptionIndex >= 0 &&
            string.Equals(serialized[firstNonOptionIndex], "show-seat", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryBuildShowArguments(serialized, firstNonOptionIndex, "Id", out var showArguments))
            {
                request = null!;
                return false;
            }

            request = new StructuredLoginctlRequest(StructuredLoginctlMode.Show, showArguments, "Id");
            return true;
        }

        if (firstNonOptionIndex >= 0 &&
            KnownNonStructuredSubcommands.Contains(serialized[firstNonOptionIndex]))
        {
            request = null!;
            return false;
        }

        if (!TryBuildListArguments(serialized, explicitSubcommandIndex: null, "list-sessions", out var defaultArguments))
        {
            request = null!;
            return false;
        }

        request = new StructuredLoginctlRequest(StructuredLoginctlMode.ListSessions, defaultArguments, null);
        return true;
    }

    private static bool TryBuildListArguments(
        IReadOnlyList<string> serialized,
        int? explicitSubcommandIndex,
        string insertedSubcommand,
        out IReadOnlyList<string> arguments)
    {
        arguments = Array.Empty<string>();

        if (ContainsOutputOption(serialized) || ContainsValueOption(serialized))
        {
            return false;
        }

        var normalized = serialized.ToList();
        var insertIndex = explicitSubcommandIndex ?? Math.Max(0, FindFirstNonOptionIndex(serialized));

        if (explicitSubcommandIndex is null)
        {
            normalized.Insert(insertIndex, insertedSubcommand);
        }

        for (var index = 0; index < normalized.Count; index++)
        {
            var argument = normalized[index];

            if (index == insertIndex && explicitSubcommandIndex is not null)
            {
                continue;
            }

            if (explicitSubcommandIndex is null && index == insertIndex)
            {
                continue;
            }

            if (!argument.StartsWith("-", StringComparison.Ordinal))
            {
                return false;
            }

            if (RequiresValue(argument) && index + 1 < normalized.Count)
            {
                index++;
            }
        }

        if (HasJsonOffMode(normalized))
        {
            return false;
        }

        if (!ContainsJsonOption(normalized))
        {
            normalized.Add("--json=short");
        }

        if (!normalized.Any(argument => string.Equals(argument, "--no-pager", StringComparison.Ordinal)))
        {
            normalized.Add("--no-pager");
        }

        if (!normalized.Any(argument => string.Equals(argument, "--no-legend", StringComparison.Ordinal)))
        {
            normalized.Add("--no-legend");
        }

        arguments = normalized;
        return true;
    }

    private static bool TryBuildShowArguments(
        IReadOnlyList<string> serialized,
        int explicitSubcommandIndex,
        string identityPropertyName,
        out IReadOnlyList<string> arguments)
    {
        arguments = Array.Empty<string>();

        if (ContainsOutputOption(serialized) || ContainsValueOption(serialized) || ContainsJsonOption(serialized))
        {
            return false;
        }

        var normalized = serialized.ToList();
        var targets = new List<string>();
        var hasIdentityProperty = false;
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
                hasIdentityProperty |= ContainsPropertyName(normalized[index + 1], identityPropertyName);
                index++;
                continue;
            }

            if (argument.StartsWith("--property=", StringComparison.Ordinal))
            {
                hasPropertyFilter = true;
                hasIdentityProperty |= ContainsPropertyName(argument["--property=".Length..], identityPropertyName);
                continue;
            }

            if (argument.StartsWith("-", StringComparison.Ordinal))
            {
                if (RequiresValue(argument) && index + 1 < normalized.Count)
                {
                    index++;
                }

                continue;
            }

            targets.Add(argument);
        }

        if (targets.Count == 0)
        {
            return false;
        }

        if (hasPropertyFilter && !hasIdentityProperty)
        {
            normalized.Add("-p");
            normalized.Add(identityPropertyName);
        }

        if (!normalized.Any(argument => string.Equals(argument, "--no-pager", StringComparison.Ordinal)))
        {
            normalized.Add("--no-pager");
        }

        arguments = normalized;
        return true;
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

            if (RequiresValue(argument) && index + 1 < arguments.Count)
            {
                index++;
            }
        }

        return -1;
    }

    private static bool RequiresValue(string argument)
    {
        return argument is "-H" or "--host" or "-M" or "--machine" or "-p" or "--property" or "-P" or "-n" or "--lines" or "-o" or "--output" ||
               argument.StartsWith("--json", StringComparison.Ordinal);
    }

    private static bool ContainsOutputOption(IReadOnlyList<string> arguments)
    {
        return arguments.Any(argument =>
            string.Equals(argument, "-o", StringComparison.Ordinal) ||
            string.Equals(argument, "--output", StringComparison.Ordinal) ||
            argument.StartsWith("--output=", StringComparison.Ordinal));
    }

    private static bool ContainsValueOption(IReadOnlyList<string> arguments)
    {
        return arguments.Any(argument =>
            string.Equals(argument, "-P", StringComparison.Ordinal) ||
            string.Equals(argument, "--value", StringComparison.Ordinal));
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

    private static async Task<LoginctlProcessResult> ExecuteStructuredAsync(
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
                code: "tosh.runtime.loginctl_command_start_failed",
                title: "Failed to start the system 'loginctl' command.");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(context.CancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(context.CancellationToken);

        await process.WaitForExitAsync(context.CancellationToken);

        return new LoginctlProcessResult(
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

    private enum StructuredLoginctlMode
    {
        ListSessions,
        ListUsers,
        ListSeats,
        Show,
    }

    private sealed record StructuredLoginctlRequest(
        StructuredLoginctlMode Mode,
        IReadOnlyList<string> ExternalArguments,
        string? IdentityPropertyName);

    private sealed record LoginctlProcessResult(
        int ExitCode,
        string StandardOutput,
        string StandardError);
}
