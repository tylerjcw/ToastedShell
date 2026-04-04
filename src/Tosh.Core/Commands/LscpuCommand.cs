using System.Diagnostics;

namespace Tosh.Core.Commands;

public sealed class LscpuCommand : ShellCommand
{
    public LscpuCommand()
        : base("lscpu", "Wraps the system lscpu utility, returning typed CPU summary, topology, and cache objects for JSON-backed invocations.", "lscpu [-B] [--hierarchic when] | lscpu -e[-all|-online|-offline] [-x] [-y] [-o columns] | lscpu -C [-B] [-o columns]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var resolvedPath = ResolveLscpuExecutable(context);
        var parsedSelection = CommandDisplaySelectionParser.Parse(context.Arguments);

        if (!TryParseStructuredRequest(parsedSelection.RemainingArguments, parsedSelection.Selection, out var request))
        {
            var external = new ExternalProcessCommand(Name, resolvedPath);

            await foreach (var item in external.ExecuteAsync(context))
            {
                yield return item;
            }

            yield break;
        }

        var result = await ExecuteStructuredLscpuAsync(context, resolvedPath, request.ExternalArguments);

        context.Runtime.SetLastExitCode(result.ExitCode);
        context.PipelineExitStatusTracker?.Record(result.ExitCode);

        if (result.ExitCode != 0)
        {
            var message = string.IsNullOrWhiteSpace(result.StandardError)
                ? "The system 'lscpu' command failed."
                : result.StandardError.Trim();

            throw context.CreateDiagnostic(
                code: "tosh::runtime::lscpu_command_failed",
                title: message);
        }

        if (!string.IsNullOrWhiteSpace(result.StandardError))
        {
            await context.Runtime.Error.WriteLineAsync(result.StandardError.TrimEnd());
        }

        switch (request.Mode)
        {
            case LscpuStructuredMode.Summary:
            {
                CpuInfo summary;

                try
                {
                    summary = LscpuJsonParser.ParseSummary(result.StandardOutput);
                }
                catch (Exception exception) when (exception is InvalidOperationException or System.Text.Json.JsonException)
                {
                    throw context.CreateDiagnostic(
                        code: "tosh::runtime::lscpu_json_parse_failed",
                        title: $"Could not parse structured 'lscpu' summary output. {exception.Message}",
                        help: "Try running the external `lscpu` command directly if you are using an output mode that does not support JSON.");
                }

                yield return CommandDisplaySelectionParser.Apply(context.Runtime, request.DisplaySelection, summary);
                yield break;
            }
            case LscpuStructuredMode.Extended:
            {
                IReadOnlyList<CpuTopologyInfo> rows;

                try
                {
                    rows = LscpuJsonParser.ParseTopology(result.StandardOutput);
                }
                catch (Exception exception) when (exception is InvalidOperationException or System.Text.Json.JsonException)
                {
                    throw context.CreateDiagnostic(
                        code: "tosh::runtime::lscpu_json_parse_failed",
                        title: $"Could not parse structured 'lscpu --extended' output. {exception.Message}",
                        help: "Try running the external `lscpu` command directly if you are using an output mode that does not support JSON.");
                }

                foreach (var row in rows)
                {
                    context.CancellationToken.ThrowIfCancellationRequested();
                    yield return CommandDisplaySelectionParser.Apply(context.Runtime, request.DisplaySelection, row);
                }

                yield break;
            }
            case LscpuStructuredMode.Caches:
            {
                IReadOnlyList<CpuCacheInfo> rows;

                try
                {
                    rows = LscpuJsonParser.ParseCaches(result.StandardOutput, request.PreferByteSizes);
                }
                catch (Exception exception) when (exception is InvalidOperationException or System.Text.Json.JsonException)
                {
                    throw context.CreateDiagnostic(
                        code: "tosh::runtime::lscpu_json_parse_failed",
                        title: $"Could not parse structured 'lscpu --caches' output. {exception.Message}",
                        help: "Try running the external `lscpu` command directly if you are using an output mode that does not support JSON.");
                }

                foreach (var row in rows)
                {
                    context.CancellationToken.ThrowIfCancellationRequested();
                    yield return CommandDisplaySelectionParser.Apply(context.Runtime, request.DisplaySelection, row);
                }

                yield break;
            }
            default:
                throw new InvalidOperationException($"Unexpected lscpu structured mode '{request.Mode}'.");
        }
    }

    private static string ResolveLscpuExecutable(CommandContext context)
    {
        var lookup = ExternalCommandResolver.Resolve(context.Runtime.CurrentDirectory, "lscpu");

        return lookup.Status switch
        {
            ExternalCommandLookupStatus.Found when lookup.ResolvedPath is not null => lookup.ResolvedPath,
            _ => throw context.CreateDiagnostic(
                code: "tosh::runtime::lscpu_command_missing",
                title: "The system 'lscpu' command was not found.",
                help: "Install util-linux or invoke the external utility by full path once it is available."),
        };
    }

    private static bool TryParseStructuredRequest(
        IReadOnlyList<object?> arguments,
        DisplayColumnSelection builtinSelection,
        out StructuredLscpuRequest request)
    {
        var serialized = arguments
            .Select(ExternalTextSerializer.SerializeArgument)
            .ToArray();

        if (serialized.Any(IsUnsupportedStructuredOption))
        {
            request = null!;
            return false;
        }

        var mode = LscpuStructuredMode.Summary;
        var explicitMode = false;
        var preferByteSizes = false;
        var explicitOutputAll = false;
        var commandOutputColumns = new List<string>();
        var externalArguments = new List<string>();

        for (var index = 0; index < serialized.Length; index++)
        {
            var argument = serialized[index];

            switch (argument)
            {
                case "-B":
                case "--bytes":
                    preferByteSizes = true;
                    externalArguments.Add(argument);
                    break;
                case "-e":
                case "--extended":
                    if (explicitMode && mode != LscpuStructuredMode.Extended)
                    {
                        request = null!;
                        return false;
                    }

                    explicitMode = true;
                    mode = LscpuStructuredMode.Extended;
                    break;
                case var _ when argument.StartsWith("--extended=", StringComparison.Ordinal):
                    if (explicitMode && mode != LscpuStructuredMode.Extended)
                    {
                        request = null!;
                        return false;
                    }

                    explicitMode = true;
                    mode = LscpuStructuredMode.Extended;
                    AddColumns(commandOutputColumns, argument["--extended=".Length..]);
                    break;
                case "-C":
                case "--caches":
                    if (explicitMode && mode != LscpuStructuredMode.Caches)
                    {
                        request = null!;
                        return false;
                    }

                    explicitMode = true;
                    mode = LscpuStructuredMode.Caches;
                    break;
                case var _ when argument.StartsWith("--caches=", StringComparison.Ordinal):
                    if (explicitMode && mode != LscpuStructuredMode.Caches)
                    {
                        request = null!;
                        return false;
                    }

                    explicitMode = true;
                    mode = LscpuStructuredMode.Caches;
                    AddColumns(commandOutputColumns, argument["--caches=".Length..]);
                    break;
                case "-o":
                case "--output":
                    if (!explicitMode || mode == LscpuStructuredMode.Summary)
                    {
                        request = null!;
                        return false;
                    }

                    if (index + 1 >= serialized.Length)
                    {
                        throw new InvalidOperationException("Option '-o' requires a comma-separated column list.");
                    }

                    AddColumns(commandOutputColumns, serialized[++index]);
                    break;
                case "--output-all":
                    if (!explicitMode || mode == LscpuStructuredMode.Summary)
                    {
                        request = null!;
                        return false;
                    }

                    explicitOutputAll = true;
                    break;
                case "-a":
                case "--all":
                case "-b":
                case "--online":
                case "-c":
                case "--offline":
                case "-x":
                case "--hex":
                case "-y":
                case "--physical":
                    externalArguments.Add(argument);
                    break;
                case "--hierarchic":
                    externalArguments.Add(argument);
                    if (index + 1 < serialized.Length)
                    {
                        externalArguments.Add(serialized[++index]);
                    }
                    break;
                default:
                    if (argument.StartsWith("--hierarchic=", StringComparison.Ordinal))
                    {
                        externalArguments.Add(argument);
                    }
                    else
                    {
                        request = null!;
                        return false;
                    }

                    break;
            }
        }

        var effectiveSelection = BuildEffectiveSelection(builtinSelection, commandOutputColumns);
        var requiresOutputAll = explicitOutputAll ||
                                commandOutputColumns.Count > 0 ||
                                builtinSelection.ShowColumns.Count > 0 ||
                                builtinSelection.ShowAll;

        var processArguments = new List<string> { "--json" };

        switch (mode)
        {
            case LscpuStructuredMode.Extended:
                processArguments.Add("--extended");
                break;
            case LscpuStructuredMode.Caches:
                processArguments.Add("--caches");
                break;
        }

        if (requiresOutputAll && mode is LscpuStructuredMode.Extended or LscpuStructuredMode.Caches)
        {
            processArguments.Add("--output-all");
        }

        processArguments.AddRange(externalArguments);

        request = new StructuredLscpuRequest(mode, processArguments, effectiveSelection, preferByteSizes);
        return true;
    }

    private static DisplayColumnSelection BuildEffectiveSelection(DisplayColumnSelection builtinSelection, IReadOnlyList<string> commandOutputColumns)
    {
        if (commandOutputColumns.Count == 0)
        {
            return builtinSelection;
        }

        if (builtinSelection.ShowAll)
        {
            return builtinSelection;
        }

        var showColumns = commandOutputColumns
            .Concat(builtinSelection.ShowColumns)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new DisplayColumnSelection(showColumns, builtinSelection.HideColumns, showAll: false);
    }

    private static bool IsUnsupportedStructuredOption(string argument)
    {
        return string.Equals(argument, "-h", StringComparison.Ordinal) ||
               string.Equals(argument, "--help", StringComparison.Ordinal) ||
               string.Equals(argument, "-V", StringComparison.Ordinal) ||
               string.Equals(argument, "--version", StringComparison.Ordinal) ||
               string.Equals(argument, "-p", StringComparison.Ordinal) ||
               string.Equals(argument, "--parse", StringComparison.Ordinal) ||
               argument.StartsWith("--parse=", StringComparison.Ordinal) ||
               string.Equals(argument, "-r", StringComparison.Ordinal) ||
               string.Equals(argument, "--raw", StringComparison.Ordinal) ||
               string.Equals(argument, "-s", StringComparison.Ordinal) ||
               string.Equals(argument, "--sysroot", StringComparison.Ordinal) ||
               argument.StartsWith("--sysroot=", StringComparison.Ordinal);
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

    private static async Task<LscpuProcessResult> ExecuteStructuredLscpuAsync(
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
                code: "tosh::runtime::lscpu_command_start_failed",
                title: "Failed to start the system 'lscpu' command.");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(context.CancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(context.CancellationToken);

        await process.WaitForExitAsync(context.CancellationToken);

        return new LscpuProcessResult(
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

    private enum LscpuStructuredMode
    {
        Summary,
        Extended,
        Caches,
    }

    private sealed record StructuredLscpuRequest(
        LscpuStructuredMode Mode,
        IReadOnlyList<string> ExternalArguments,
        DisplayColumnSelection DisplaySelection,
        bool PreferByteSizes);

    private sealed record LscpuProcessResult(
        int ExitCode,
        string StandardOutput,
        string StandardError);
}
