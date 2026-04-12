using System.Diagnostics;

namespace Tosh.Core.Commands;

[CommandCategory("Process")]
[CommandArgument("[-p pid[,pid...]]", "Optionally restrict the query to specific processes.", Required = false)]
[CommandOption("-l", "Include thread-level rows with a `TID` column.")]
[CommandOption("-p <pid(s)>", "Restrict the query to one or more process ids.")]
[CommandOption("-i[4|6]", "Restrict the result set to IPv4 and/or IPv6 sockets.")]
[CommandOption("-o <columns>", "Select lsfd-style columns such as `COMMAND,PID,FD,TYPE,NAME`.")]
[CommandOption("--summary[=only|append|never]", "Include or isolate lsfd summary counters alongside structured descriptor rows.")]
[CommandOption("--show <columns>", "Use ToSh display-only column selection without changing the underlying `FileDescriptorInfo` objects.")]
[CommandOption("--hide <columns>", "Hide display columns while keeping the full typed rows in the pipeline.")]
[CommandOption("--show-all", "Expose every selectable structured lsfd column discoverable from the local `lsfd -H` catalog.")]
[CommandExample("lsfd", Title = "Browse open file descriptors as typed rows")]
[CommandExample("lsfd -p 1 -o COMMAND,PID,ASSOC,TYPE,NAME", Title = "Inspect a specific process with explicit lsfd columns")]
[CommandExample("lsfd --summary=only", Title = "Show typed summary counters only")]
[CommandNote("ToSh wraps `lsfd --json` so open-file-descriptor rows stay typed in the pipeline. `--summary=only` yields typed counters, and `--summary=append` returns both row and summary objects. Text-format-only modes like `--raw`, `--noheadings`, filter expressions, and custom counters currently fall back to the external `lsfd` utility unchanged.")]
[CommandOutput("Returns typed open-file-descriptor rows and, when summary mode is enabled, typed counter rows describing totals such as open files and sockets.")]
[PipelineInput(Description = "The structured `lsfd` builtin is explicit-arg-first and does not currently consume pipeline input.")]
public sealed class LsfdCommand : ShellCommand
{
    private static readonly string[] DefaultColumns =
    [
        "COMMAND",
        "PID",
        "USER",
        "ASSOC",
        "XMODE",
        "TYPE",
        "SOURCE",
        "MNTID",
        "INODE",
        "NAME",
    ];

    private static readonly string[] ThreadColumns =
    [
        "COMMAND",
        "PID",
        "TID",
        "USER",
        "ASSOC",
        "XMODE",
        "TYPE",
        "SOURCE",
        "MNTID",
        "INODE",
        "NAME",
    ];

    public LsfdCommand()
        : base("lsfd", "Wraps the system lsfd utility, returning typed open-file-descriptor objects for JSON-backed invocations.", "lsfd [-l] [-p pid[,pid...]] [-i[4|6]] [-o columns] [--summary[=only|append|never]]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (OperatingSystem.IsWindows())
        {
            throw context.CreateDiagnostic(
                code: "tosh::runtime::command_windows_unavailable",
                title: $"'{Name}' is not available on Windows.",
                help: "This command lists open Linux file descriptors via /proc and the kernel fd table, which have no Windows equivalent.");
        }

        var resolvedPath = ResolveLsfdExecutable(context);
        var parsedSelection = CommandDisplaySelectionParser.Parse(context.Arguments);

        if (!TryParseStructuredRequest(context, resolvedPath, parsedSelection.RemainingArguments, parsedSelection.Selection, out var request))
        {
            var external = new ExternalProcessCommand(Name, resolvedPath);

            await foreach (var item in external.ExecuteAsync(context))
            {
                yield return item;
            }

            yield break;
        }

        var result = await ExecuteStructuredLsfdAsync(context, resolvedPath, request.ExternalArguments);

        context.Runtime.SetLastExitCode(result.ExitCode);
        context.PipelineExitStatusTracker?.Record(result.ExitCode);

        if (result.ExitCode != 0)
        {
            var message = string.IsNullOrWhiteSpace(result.StandardError)
                ? "The system 'lsfd' command failed."
                : result.StandardError.Trim();

            throw context.CreateDiagnostic(
                code: "tosh::runtime::lsfd_command_failed",
                title: message);
        }

        if (!string.IsNullOrWhiteSpace(result.StandardError))
        {
            await context.Runtime.Error.WriteLineAsync(result.StandardError.TrimEnd());
        }

        LsfdParseResult parsed;

        try
        {
            parsed = LsfdJsonParser.Parse(result.StandardOutput);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.Text.Json.JsonException)
        {
            throw context.CreateDiagnostic(
                code: "tosh::runtime::lsfd_json_parse_failed",
                title: $"Could not parse structured 'lsfd' output. {exception.Message}",
                help: "Try running the external `lsfd` command directly if you are using an output mode that does not support JSON.");
        }

        foreach (var row in parsed.Rows)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            yield return CommandDisplaySelectionParser.Apply(context.Runtime, request.RowSelection, row);
        }

        var counterSelection = parsed.Rows.Count == 0
            ? request.CounterSelection
            : null;

        foreach (var counter in parsed.Summary)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            yield return counterSelection is null
                ? counter
                : CommandDisplaySelectionParser.Apply(context.Runtime, counterSelection, counter);
        }
    }

    private static string ResolveLsfdExecutable(CommandContext context)
    {
        var lookup = ExternalCommandResolver.Resolve(context.Runtime.CurrentDirectory, "lsfd");

        return lookup.Status switch
        {
            ExternalCommandLookupStatus.Found when lookup.ResolvedPath is not null => lookup.ResolvedPath,
            _ => throw context.CreateDiagnostic(
                code: "tosh::runtime::lsfd_command_missing",
                title: "The system 'lsfd' command was not found.",
                help: "Install util-linux or invoke the external utility by full path once it is available."),
        };
    }

    private static bool TryParseStructuredRequest(
        CommandContext context,
        string resolvedPath,
        IReadOnlyList<object?> arguments,
        DisplayColumnSelection builtinSelection,
        out StructuredLsfdRequest request)
    {
        var serialized = arguments
            .Select(ExternalTextSerializer.SerializeArgument)
            .ToArray();

        if (serialized.Any(IsUnsupportedStructuredOption))
        {
            request = null!;
            return false;
        }

        var externalArguments = new List<string> { "--json" };
        var commandOutputColumns = new List<string>();
        var includeThreads = false;

        for (var index = 0; index < serialized.Length; index++)
        {
            var argument = serialized[index];

            switch (argument)
            {
                case "-l":
                case "--threads":
                    includeThreads = true;
                    externalArguments.Add(argument);
                    break;
                case "-p":
                case "--pid":
                    externalArguments.Add(argument);
                    if (index + 1 >= serialized.Length)
                    {
                        throw new InvalidOperationException("Option '--pid' requires a pid list.");
                    }

                    externalArguments.Add(serialized[++index]);
                    break;
                case "-i":
                case "-i4":
                case "-i6":
                case "--inet":
                    externalArguments.Add(argument);
                    break;
                case var _ when argument.StartsWith("--inet=", StringComparison.Ordinal):
                    externalArguments.Add(argument);
                    break;
                case "--summary":
                    externalArguments.Add(argument);
                    if (index + 1 < serialized.Length && !serialized[index + 1].StartsWith("-", StringComparison.Ordinal))
                    {
                        externalArguments.Add(serialized[++index]);
                    }
                    break;
                case var _ when argument.StartsWith("--summary=", StringComparison.Ordinal):
                    externalArguments.Add(argument);
                    break;
                case "-o":
                case "--output":
                    if (index + 1 >= serialized.Length)
                    {
                        throw new InvalidOperationException("Option '-o' requires a comma-separated column list.");
                    }

                    AddColumns(commandOutputColumns, serialized[++index]);
                    break;
                default:
                    request = null!;
                    return false;
            }
        }

        var rowsRequested = !IsSummaryOnly(externalArguments);

        if (rowsRequested)
        {
            var outputColumns = ResolveOutputColumns(context, resolvedPath, includeThreads, builtinSelection, commandOutputColumns);

            if (outputColumns.Count > 0 &&
                !IsDefaultSelection(outputColumns, includeThreads))
            {
                externalArguments.Add("-o");
                externalArguments.Add(string.Join(",", outputColumns));
            }
        }

        request = new StructuredLsfdRequest(
            externalArguments,
            BuildRowSelection(builtinSelection, commandOutputColumns),
            builtinSelection);
        return true;
    }

    private static DisplayColumnSelection BuildRowSelection(DisplayColumnSelection builtinSelection, IReadOnlyList<string> commandOutputColumns)
    {
        if (builtinSelection.ShowAll)
        {
            return builtinSelection;
        }

        if (commandOutputColumns.Count == 0)
        {
            return builtinSelection;
        }

        var showColumns = commandOutputColumns
            .Concat(builtinSelection.ShowColumns)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new DisplayColumnSelection(showColumns, builtinSelection.HideColumns, showAll: false);
    }

    private static IReadOnlyList<string> ResolveOutputColumns(
        CommandContext context,
        string resolvedPath,
        bool includeThreads,
        DisplayColumnSelection builtinSelection,
        IReadOnlyList<string> commandOutputColumns)
    {
        if (builtinSelection.ShowAll)
        {
            return GetAvailableColumns(context, resolvedPath);
        }

        var columns = new List<string>(includeThreads ? ThreadColumns : DefaultColumns);
        AddColumns(columns, commandOutputColumns);
        AddColumns(columns, builtinSelection.ShowColumns);
        return columns;
    }

    private static IReadOnlyList<string> GetAvailableColumns(CommandContext context, string resolvedPath)
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
        startInfo.ArgumentList.Add("-H");

        using var process = Process.Start(startInfo) ?? throw context.CreateDiagnostic(
            code: "tosh::runtime::lsfd_command_start_failed",
            title: "Failed to start the system 'lsfd' command while discovering columns.");

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            var message = string.IsNullOrWhiteSpace(stderr)
                ? "The system 'lsfd -H' command failed while listing columns."
                : stderr.Trim();

            throw context.CreateDiagnostic(
                code: "tosh::runtime::lsfd_columns_failed",
                title: message);
        }

        return stdout
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray()!;
    }

    private static bool IsSummaryOnly(IReadOnlyList<string> arguments)
    {
        foreach (var argument in arguments)
        {
            if (string.Equals(argument, "--summary=only", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsDefaultSelection(IReadOnlyList<string> columns, bool includeThreads)
    {
        var baseline = includeThreads ? ThreadColumns : DefaultColumns;
        return columns.SequenceEqual(baseline, StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsUnsupportedStructuredOption(string argument)
    {
        return string.Equals(argument, "-H", StringComparison.Ordinal) ||
               string.Equals(argument, "--list-columns", StringComparison.Ordinal) ||
               string.Equals(argument, "-h", StringComparison.Ordinal) ||
               string.Equals(argument, "--help", StringComparison.Ordinal) ||
               string.Equals(argument, "-V", StringComparison.Ordinal) ||
               string.Equals(argument, "--version", StringComparison.Ordinal) ||
               string.Equals(argument, "-n", StringComparison.Ordinal) ||
               string.Equals(argument, "--noheadings", StringComparison.Ordinal) ||
               string.Equals(argument, "-r", StringComparison.Ordinal) ||
               string.Equals(argument, "--raw", StringComparison.Ordinal) ||
               string.Equals(argument, "-u", StringComparison.Ordinal) ||
               string.Equals(argument, "--notruncate", StringComparison.Ordinal) ||
               string.Equals(argument, "-Q", StringComparison.Ordinal) ||
               string.Equals(argument, "--filter", StringComparison.Ordinal) ||
               string.Equals(argument, "--debug-filter", StringComparison.Ordinal) ||
               string.Equals(argument, "-C", StringComparison.Ordinal) ||
               string.Equals(argument, "--counter", StringComparison.Ordinal) ||
               string.Equals(argument, "--dump-counters", StringComparison.Ordinal) ||
               string.Equals(argument, "--hyperlink", StringComparison.Ordinal) ||
               argument.StartsWith("--hyperlink=", StringComparison.Ordinal) ||
               string.Equals(argument, "--_drop-privilege", StringComparison.Ordinal);
    }

    private static void AddColumns(List<string> target, IEnumerable<string> columns)
    {
        foreach (var column in columns)
        {
            if (!target.Any(existing => string.Equals(existing, column, StringComparison.OrdinalIgnoreCase)))
            {
                target.Add(column);
            }
        }
    }

    private static void AddColumns(List<string> target, string specification)
    {
        if (string.IsNullOrWhiteSpace(specification))
        {
            return;
        }

        foreach (var candidate in specification.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (!target.Any(existing => string.Equals(existing, candidate, StringComparison.OrdinalIgnoreCase)))
            {
                target.Add(candidate);
            }
        }
    }

    private static async Task<LsfdProcessResult> ExecuteStructuredLsfdAsync(
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
                code: "tosh::runtime::lsfd_command_start_failed",
                title: "Failed to start the system 'lsfd' command.");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(context.CancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(context.CancellationToken);

        await process.WaitForExitAsync(context.CancellationToken);

        return new LsfdProcessResult(
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

    private sealed record StructuredLsfdRequest(
        IReadOnlyList<string> ExternalArguments,
        DisplayColumnSelection RowSelection,
        DisplayColumnSelection CounterSelection);

    private sealed record LsfdProcessResult(
        int ExitCode,
        string StandardOutput,
        string StandardError);
}
