using System.Diagnostics;

using Tosh.Runtime;

namespace Tosh.Stdlib.Filesystem;

[Stdlib(StdlibCategory.Filesystem)]
[CommandCategory("Filesystem")]
[CommandArgument("[path-or-device ...]", "Optional mountpoints or source devices to match.", Required = false, TypeName = "path-like|string")]
[CommandOption("-S <source>", "Match a source device or source specification.")]
[CommandOption("-T <target>", "Match the filesystem that contains a target path.")]
[CommandOption("-M <mountpoint>", "Match a specific mountpoint.")]
[CommandOption("-t <types>", "Limit the result set to specific filesystem types.")]
[CommandOption("-O <options>", "Limit the result set to mounts with matching options.")]
[CommandOption("-R", "Include submounts for matching filesystems.")]
[CommandOption("-U", "Drop duplicate targets.")]
[CommandOption("-l", "Return a flattened list instead of a hierarchy.")]
[CommandOption("-A", "Disable built-in filters and include everything findmnt normally hides.")]
[CommandOption("-b", "Render size-oriented columns in raw bytes.")]
[CommandOption("-D", "Use a `df`-style display preset.")]
[CommandOption("-I", "Use a `df -i`-style inode display preset.")]
[CommandOption("-o <columns>", "Select findmnt-style output columns such as `TARGET,SOURCE,FSTYPE,OPTIONS`.")]
[CommandOption("--output-all", "Expose every selectable structured findmnt column.")]
[CommandExample("findmnt", Title = "Browse the mounted-filesystem tree as typed objects")]
[CommandExample("findmnt -l | where _.Target.StartsWith(\"/run\")", Title = "Flatten the mount tree and filter by target path")]
[CommandExample("findmnt -o TARGET,SOURCE,FSTYPE", Title = "Pick explicit findmnt-style display columns without changing the underlying objects")]
[CommandNote("ToSh wraps `findmnt --json --bytes --output-all` so mounted filesystems stay as typed objects in the pipeline while the default renderer can still show them as a tree with columns. The default result is hierarchical, so use `-l` when you want flat filtering in a pipeline, and shell-facing aliases like `FsType` and `FsRoot` match the visible column names. Output-format-only modes like `--pairs`, `--raw`, `--noheadings`, polling, and verification currently fall back to the external `findmnt` utility unchanged.")]
[CommandOutput("Returns reusable mounted-filesystem objects with nested child mounts when the underlying `findmnt --json` output is hierarchical.")]
[PipelineInput(Description = "The structured `findmnt` builtin is explicit-arg-first and does not currently consume pipeline input.")]
public sealed class FindmntCommand : ShellCommand
{
    public FindmntCommand()
        : base("findmnt", "Wraps the system findmnt utility, returning typed mounted-filesystem objects for JSON-backed invocations.", "findmnt [-AflbR] [-S source] [-T target] [-M mountpoint] [-t types] [-O options] [-o columns] [path-or-device ...]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        // On Windows there is no findmnt; enumerate volumes via DriveInfo instead.
        if (OperatingSystem.IsWindows())
        {
            var winSelection = CommandDisplaySelectionParser.Parse(
                context.Arguments,
                showOptionAliases: ["-o", "--output"],
                showAllAliases: ["--output-all"]);

            var winMounts = WindowsMountServices.GetMounts();

            foreach (var mount in winMounts)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                yield return CommandDisplaySelectionParser.Apply(
                    context.Runtime,
                    winSelection.Selection,
                    mount);
            }

            yield break;
        }

        var resolvedPath = ResolveFindmntExecutable(context);
        var parsedSelection = CommandDisplaySelectionParser.Parse(
            context.Arguments,
            showOptionAliases: ["-o", "--output"],
            showAllAliases: ["--output-all"]);

        if (!TryParseStructuredRequest(parsedSelection.RemainingArguments, out var request))
        {
            var external = new ExternalProcessCommand(Name, resolvedPath);

            await foreach (var item in external.ExecuteAsync(context))
            {
                yield return item;
            }

            yield break;
        }

        var result = await ExecuteStructuredFindmntAsync(context, resolvedPath, request.ExternalArguments);

        context.Runtime.SetLastExitCode(result.ExitCode);
        context.PipelineExitStatusTracker?.Record(result.ExitCode);

        if (result.ExitCode != 0)
        {
            var message = string.IsNullOrWhiteSpace(result.StandardError)
                ? "The system 'findmnt' command failed."
                : result.StandardError.Trim();

            throw context.CreateDiagnostic(
                code: "tosh.runtime.findmnt_command_failed",
                title: message);
        }

        IReadOnlyList<MountInfo> mounts;

        try
        {
            mounts = FindmntJsonParser.ParseMounts(result.StandardOutput)
                .Select(item => item.WithDisplayPreferences(request.PreferByteSizes))
                .ToArray();
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.Text.Json.JsonException)
        {
            throw context.CreateDiagnostic(
                code: "tosh.runtime.findmnt_json_parse_failed",
                title: $"Could not parse structured 'findmnt' output. {exception.Message}",
                help: "Try running the external `findmnt` command directly if you are using an output mode that does not support JSON.");
        }

        if (!string.IsNullOrWhiteSpace(result.StandardError))
        {
            await context.Runtime.Error.WriteLineAsync(result.StandardError.TrimEnd());
        }

        var effectiveSelection = request.PresetColumns.Count > 0 &&
                                 !parsedSelection.Selection.ShowAll &&
                                 parsedSelection.Selection.ShowColumns.Count == 0
            ? new DisplayColumnSelection(
                request.PresetColumns,
                parsedSelection.Selection.HideColumns,
                showAll: false)
            : parsedSelection.Selection;

        foreach (var mount in mounts)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            yield return CommandDisplaySelectionParser.Apply(context.Runtime, effectiveSelection, mount);
        }
    }

    private static string ResolveFindmntExecutable(CommandContext context)
    {
        var lookup = ExternalCommandResolver.Resolve(context.Runtime.CurrentDirectory, "findmnt");

        return lookup.Status switch
        {
            ExternalCommandLookupStatus.Found when lookup.ResolvedPath is not null => lookup.ResolvedPath,
            _ => throw context.CreateDiagnostic(
                code: "tosh.runtime.findmnt_command_missing",
                title: "The system 'findmnt' command was not found.",
                help: "Install util-linux or invoke the external utility by full path once it is available."),
        };
    }

    private static bool TryParseStructuredRequest(
        IReadOnlyList<object?> arguments,
        out StructuredFindmntRequest request)
    {
        var serialized = arguments
            .Select(ExternalTextSerializer.SerializeArgument)
            .ToArray();

        if (serialized.Any(IsUnsupportedStructuredOption))
        {
            request = null!;
            return false;
        }

        var externalArguments = new List<string>
        {
            "--json",
            "--bytes",
            "--output-all",
        };

        var presetColumns = new List<string>();
        var preferByteSizes = false;

        for (var index = 0; index < serialized.Length; index++)
        {
            var argument = serialized[index];

            switch (argument)
            {
                case "-b":
                case "--bytes":
                    preferByteSizes = true;
                    break;
                case "-D":
                case "--df":
                    AddPresetColumns(presetColumns, "TARGET", "SOURCE", "FSTYPE", "SIZE", "USED", "AVAIL", "USE%");
                    externalArguments.Add(argument);
                    break;
                case "-I":
                case "--dfi":
                    AddPresetColumns(presetColumns, "TARGET", "SOURCE", "INO.TOTAL", "INO.USED", "INO.AVAIL", "INO.USE%");
                    externalArguments.Add(argument);
                    break;
                case "-o":
                case "--output":
                    index++;
                    break;
                case "--output-all":
                    break;
                case "-H":
                case "--list-columns":
                case "-h":
                case "--help":
                case "-V":
                case "--version":
                    request = null!;
                    return false;
                default:
                    if (!IsSelectionOnlyOption(argument))
                    {
                        externalArguments.Add(argument);
                    }
                    break;
            }
        }

        request = new StructuredFindmntRequest(externalArguments, presetColumns, preferByteSizes);
        return true;
    }

    private static bool IsSelectionOnlyOption(string argument)
    {
        return string.Equals(argument, "-b", StringComparison.Ordinal) ||
               string.Equals(argument, "--bytes", StringComparison.Ordinal) ||
               string.Equals(argument, "-D", StringComparison.Ordinal) ||
               string.Equals(argument, "--df", StringComparison.Ordinal) ||
               string.Equals(argument, "-I", StringComparison.Ordinal) ||
               string.Equals(argument, "--dfi", StringComparison.Ordinal);
    }

    private static bool IsUnsupportedStructuredOption(string argument)
    {
        return string.Equals(argument, "-a", StringComparison.Ordinal) ||
               string.Equals(argument, "--ascii", StringComparison.Ordinal) ||
               string.Equals(argument, "-n", StringComparison.Ordinal) ||
               string.Equals(argument, "--noheadings", StringComparison.Ordinal) ||
               string.Equals(argument, "-P", StringComparison.Ordinal) ||
               string.Equals(argument, "--pairs", StringComparison.Ordinal) ||
               string.Equals(argument, "-r", StringComparison.Ordinal) ||
               string.Equals(argument, "--raw", StringComparison.Ordinal) ||
               string.Equals(argument, "-u", StringComparison.Ordinal) ||
               string.Equals(argument, "--notruncate", StringComparison.Ordinal) ||
               string.Equals(argument, "-y", StringComparison.Ordinal) ||
               string.Equals(argument, "--shell", StringComparison.Ordinal) ||
               string.Equals(argument, "-p", StringComparison.Ordinal) ||
               string.Equals(argument, "--poll", StringComparison.Ordinal) ||
               string.Equals(argument, "-x", StringComparison.Ordinal) ||
               string.Equals(argument, "--verify", StringComparison.Ordinal) ||
               string.Equals(argument, "--verbose", StringComparison.Ordinal) ||
               string.Equals(argument, "--vfs-all", StringComparison.Ordinal);
    }

    private static void AddPresetColumns(List<string> target, params string[] columns)
    {
        foreach (var column in columns)
        {
            if (!target.Any(existing => string.Equals(existing, column, StringComparison.OrdinalIgnoreCase)))
            {
                target.Add(column);
            }
        }
    }

    private static async Task<FindmntProcessResult> ExecuteStructuredFindmntAsync(
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
                code: "tosh.runtime.findmnt_command_start_failed",
                title: "Failed to start the system 'findmnt' command.");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(context.CancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(context.CancellationToken);

        await process.WaitForExitAsync(context.CancellationToken);

        return new FindmntProcessResult(
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

    private sealed record StructuredFindmntRequest(
        IReadOnlyList<string> ExternalArguments,
        IReadOnlyList<string> PresetColumns,
        bool PreferByteSizes);

    private sealed record FindmntProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
