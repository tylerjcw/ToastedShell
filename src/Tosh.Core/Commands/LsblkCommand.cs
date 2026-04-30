using System.Diagnostics;

namespace Tosh.Core.Commands;

[Stdlib(StdlibCategory.Filesystem)]
[CommandCategory("Filesystem")]
[CommandArgument("[device ...]", "Optional device paths or names to scope the query to.", Required = false, TypeName = "path-like|string")]
[CommandOption("-a", "Include empty devices.")]
[CommandOption("-A", "Hide empty devices.")]
[CommandOption("-d", "Suppress dependencies and child devices.")]
[CommandOption("-b", "Render size-oriented columns in raw bytes.")]
[CommandOption("-f", "Use the filesystem-oriented display preset.")]
[CommandOption("-m", "Use the permissions-oriented display preset.")]
[CommandOption("-t", "Use the topology-oriented display preset.")]
[CommandOption("-D", "Use the discard-oriented display preset.")]
[CommandOption("-z", "Use the zoned-device display preset.")]
[CommandOption("-M", "Use the model/serial/vendor display preset.")]
[CommandOption("-L", "Use the label/UUID/partition display preset.")]
[CommandOption("-p", "Show full device paths.")]
[CommandOption("-S", "Restrict the query to SCSI devices.")]
[CommandOption("-N", "Restrict the query to NVMe devices.")]
[CommandOption("-v", "Restrict the query to virtio devices.")]
[CommandOption("-I <majors>", "Include only the specified major numbers.")]
[CommandOption("-e <majors>", "Exclude the specified major numbers.")]
[CommandOption("-x <column>", "Sort by an lsblk column name such as `NAME`, `SIZE`, or `PATH`.")]
[CommandOption("-o <columns>", "Select lsblk-style output columns such as `NAME,SIZE,FSTYPE,MOUNTPOINTS`.")]
[CommandOption("-O", "Expose every selectable structured lsblk column.")]
[CommandExample("lsblk", Title = "Browse block devices as a tree-with-columns object table")]
[CommandExample("lsblk -l -f | where _.FsType == \"ntfs\"", Title = "Flatten the block-device view and filter by filesystem type")]
[CommandExample("lsblk -o NAME,PATH,SIZE", Title = "Pick explicit lsblk-style display columns without changing the underlying objects")]
[CommandNote("ToSh wraps `lsblk --json --bytes --output-all` so block devices stay as typed objects in the pipeline while the default renderer can still show them as a tree with columns. The default result is hierarchical, so use `-l` when you want flat filtering in a pipeline, and shell-facing aliases like `FsType`, `FsVer`, and `FsAvail` match the visible column names. Output-format-only flags like `--pairs`, `--raw`, and `--noheadings` currently fall back to the external `lsblk` utility unchanged.")]
[CommandOutput("Returns reusable block-device objects with nested child devices when the underlying `lsblk --json` output is hierarchical.")]
[PipelineInput(Description = "The structured `lsblk` builtin is explicit-arg-first and does not currently consume pipeline input.")]
public sealed class LsblkCommand : ShellCommand
{
    public LsblkCommand()
        : base("lsblk", "Wraps the system lsblk utility, returning typed block-device objects for JSON-backed invocations.", "lsblk [-aAdbflmpStzDNv] [-I list] [-e list] [-x column] [-o columns] [device ...]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        // On Windows there is no lsblk; enumerate volumes via DriveInfo instead.
        if (OperatingSystem.IsWindows())
        {
            var winSelection = CommandDisplaySelectionParser.Parse(
                context.Arguments,
                showOptionAliases: ["-o", "--output"],
                showAllAliases: ["-O", "--output-all"]);

            var winDevices = WindowsBlockDeviceServices.GetBlockDevices();

            foreach (var device in winDevices)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                yield return CommandDisplaySelectionParser.Apply(
                    context.Runtime,
                    winSelection.Selection,
                    device);
            }

            yield break;
        }

        var resolvedPath = ResolveLsblkExecutable(context);
        var parsedSelection = CommandDisplaySelectionParser.Parse(
            context.Arguments,
            showOptionAliases: ["-o", "--output"],
            showAllAliases: ["-O", "--output-all"]);

        if (!TryParseStructuredRequest(parsedSelection.RemainingArguments, out var request))
        {
            var external = new ExternalProcessCommand(Name, resolvedPath);

            await foreach (var item in external.ExecuteAsync(context))
            {
                yield return item;
            }

            yield break;
        }

        var result = await ExecuteStructuredLsblkAsync(context, resolvedPath, request.ExternalArguments);

        context.Runtime.SetLastExitCode(result.ExitCode);
        context.PipelineExitStatusTracker?.Record(result.ExitCode);

        if (result.ExitCode != 0)
        {
            var message = string.IsNullOrWhiteSpace(result.StandardError)
                ? "The system 'lsblk' command failed."
                : result.StandardError.Trim();

            throw context.CreateDiagnostic(
                code: "tosh.runtime.lsblk_command_failed",
                title: message);
        }

        IReadOnlyList<BlockDeviceInfo> devices;

        try
        {
            devices = LsblkJsonParser.ParseDevices(result.StandardOutput)
                .Select(device => device.WithDisplayPreferences(request.PreferByteSizes))
                .ToArray();
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.Text.Json.JsonException)
        {
            throw context.CreateDiagnostic(
                code: "tosh.runtime.lsblk_json_parse_failed",
                title: $"Could not parse structured 'lsblk' output. {exception.Message}",
                help: "Try running the external `lsblk` command directly if you are using an output mode that does not support JSON.");
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

        foreach (var device in devices)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            yield return CommandDisplaySelectionParser.Apply(context.Runtime, effectiveSelection, device);
        }
    }

    private static string ResolveLsblkExecutable(CommandContext context)
    {
        var lookup = ExternalCommandResolver.Resolve(context.Runtime.CurrentDirectory, "lsblk");

        return lookup.Status switch
        {
            ExternalCommandLookupStatus.Found when lookup.ResolvedPath is not null => lookup.ResolvedPath,
            _ => throw context.CreateDiagnostic(
                code: "tosh.runtime.lsblk_command_missing",
                title: "The system 'lsblk' command was not found.",
                help: "Install util-linux or invoke the external utility by full path once it is available."),
        };
    }

    private static bool TryParseStructuredRequest(
        IReadOnlyList<object?> arguments,
        out StructuredLsblkRequest request)
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
                case "-f":
                case "--fs":
                    AddPresetColumns(presetColumns, "NAME", "FSTYPE", "FSVER", "LABEL", "UUID", "FSAVAIL", "FSUSED", "FSUSE%", "MOUNTPOINTS");
                    break;
                case "-m":
                case "--perms":
                    AddPresetColumns(presetColumns, "NAME", "SIZE", "OWNER", "GROUP", "MODE");
                    break;
                case "-t":
                case "--topology":
                    AddPresetColumns(presetColumns, "NAME", "ALIGNMENT", "MIN-IO", "OPT-IO", "LOG-SEC", "PHY-SEC", "ROTA", "SCHED", "RQ-SIZE");
                    break;
                case "-D":
                case "--discard":
                    AddPresetColumns(presetColumns, "NAME", "DISC-ALN", "DISC-GRAN", "DISC-MAX", "DISC-ZERO", "WSAME");
                    break;
                case "-z":
                case "--zoned":
                    AddPresetColumns(presetColumns, "NAME", "ZONED", "ZONE-SZ", "ZONE-WGRAN", "ZONE-APP", "ZONE-NR", "ZONE-OMAX", "ZONE-AMAX");
                    break;
                case "-M":
                case "--model":
                    AddPresetColumns(presetColumns, "NAME", "SIZE", "MODEL", "SERIAL", "VENDOR", "TRAN", "HCTL", "STATE");
                    break;
                case "-L":
                case "--label":
                    AddPresetColumns(presetColumns, "NAME", "TYPE", "LABEL", "UUID", "PTTYPE", "PARTLABEL", "PARTUUID", "PARTTYPE");
                    break;
                case "-o":
                case "--output":
                    index++;
                    break;
                case "-O":
                case "--output-all":
                    break;
                case "-x":
                case "--sort":
                case "-I":
                case "--include":
                case "-e":
                case "--exclude":
                case "-T":
                case "--tree":
                    externalArguments.Add(argument);
                    if (index + 1 < serialized.Length)
                    {
                        externalArguments.Add(serialized[++index]);
                    }
                    break;
                default:
                    if (argument.StartsWith("--sort=", StringComparison.Ordinal) ||
                        argument.StartsWith("--include=", StringComparison.Ordinal) ||
                        argument.StartsWith("--exclude=", StringComparison.Ordinal) ||
                        argument.StartsWith("--tree=", StringComparison.Ordinal))
                    {
                        externalArguments.Add(argument);
                    }
                    else if (!IsSelectionOnlyOption(argument))
                    {
                        externalArguments.Add(argument);
                    }
                    break;
            }
        }

        request = new StructuredLsblkRequest(externalArguments, presetColumns, preferByteSizes);
        return true;
    }

    private static bool IsSelectionOnlyOption(string argument)
    {
        return string.Equals(argument, "-b", StringComparison.Ordinal) ||
               string.Equals(argument, "--bytes", StringComparison.Ordinal) ||
               string.Equals(argument, "-f", StringComparison.Ordinal) ||
               string.Equals(argument, "--fs", StringComparison.Ordinal) ||
               string.Equals(argument, "-m", StringComparison.Ordinal) ||
               string.Equals(argument, "--perms", StringComparison.Ordinal) ||
               string.Equals(argument, "-t", StringComparison.Ordinal) ||
               string.Equals(argument, "--topology", StringComparison.Ordinal) ||
               string.Equals(argument, "-D", StringComparison.Ordinal) ||
               string.Equals(argument, "--discard", StringComparison.Ordinal) ||
               string.Equals(argument, "-z", StringComparison.Ordinal) ||
               string.Equals(argument, "--zoned", StringComparison.Ordinal) ||
               string.Equals(argument, "-M", StringComparison.Ordinal) ||
               string.Equals(argument, "--model", StringComparison.Ordinal) ||
               string.Equals(argument, "-L", StringComparison.Ordinal) ||
               string.Equals(argument, "--label", StringComparison.Ordinal);
    }

    private static bool IsUnsupportedStructuredOption(string argument)
    {
        return string.Equals(argument, "-h", StringComparison.Ordinal) ||
               string.Equals(argument, "--help", StringComparison.Ordinal) ||
               string.Equals(argument, "-H", StringComparison.Ordinal) ||
               string.Equals(argument, "--list-columns", StringComparison.Ordinal) ||
               string.Equals(argument, "-V", StringComparison.Ordinal) ||
               string.Equals(argument, "--version", StringComparison.Ordinal) ||
               string.Equals(argument, "-P", StringComparison.Ordinal) ||
               string.Equals(argument, "--pairs", StringComparison.Ordinal) ||
               string.Equals(argument, "-Q", StringComparison.Ordinal) ||
               string.Equals(argument, "--filter", StringComparison.Ordinal) ||
               string.Equals(argument, "--highlight", StringComparison.Ordinal) ||
               string.Equals(argument, "--ct-filter", StringComparison.Ordinal) ||
               string.Equals(argument, "--ct", StringComparison.Ordinal) ||
               string.Equals(argument, "-r", StringComparison.Ordinal) ||
               string.Equals(argument, "--raw", StringComparison.Ordinal) ||
               string.Equals(argument, "-n", StringComparison.Ordinal) ||
               string.Equals(argument, "--noheadings", StringComparison.Ordinal) ||
               string.Equals(argument, "-i", StringComparison.Ordinal) ||
               string.Equals(argument, "--ascii", StringComparison.Ordinal) ||
               string.Equals(argument, "-w", StringComparison.Ordinal) ||
               string.Equals(argument, "--width", StringComparison.Ordinal) ||
               string.Equals(argument, "-y", StringComparison.Ordinal) ||
               string.Equals(argument, "--shell", StringComparison.Ordinal);
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

    private static async Task<LsblkProcessResult> ExecuteStructuredLsblkAsync(
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
                code: "tosh.runtime.lsblk_command_start_failed",
                title: "Failed to start the system 'lsblk' command.");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(context.CancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(context.CancellationToken);

        await process.WaitForExitAsync(context.CancellationToken);

        return new LsblkProcessResult(
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

    private sealed record StructuredLsblkRequest(
        IReadOnlyList<string> ExternalArguments,
        IReadOnlyList<string> PresetColumns,
        bool PreferByteSizes);

    private sealed record LsblkProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
