using Tosh.Core.Commands;

namespace Tosh.Core.Commands;

[Stdlib(StdlibCategory.Concurrency)]
[CommandCategory("Concurrency")]
[CommandArgument("command", "External command name or path to start as a background job.")]
[CommandArgument("args", "Arguments to pass to the external command.", Required = false)]
[CommandOption("-f, --foreground", "Run the process in the foreground (terminal passthrough) instead of as a background job.")]
[CommandExample("spawn dotnet --version", Title = "Start an external command in the background")]
[CommandExample("echo hello | spawn cat", Title = "Feed pipeline input into a background process")]
[CommandExample("spawn --foreground $bin_path --safe -c exit", Title = "Run a variable-path binary in the foreground")]
[CommandOutput("Returns a ShellJobInfo object for the started background job, or nothing when --foreground is used.")]
[PipelineInput(AcceptsScalar = true, AcceptsList = true, Description = "Optional pipeline input forwarded to the spawned process stdin.")]
[CommandNote("Use jobs to list active jobs and wait-for to await completion.")]
public sealed class SpawnCommand : ShellCommand
{
    public SpawnCommand()
        : base("spawn", "Starts an external command as a background job, or in the foreground with --foreground.", "spawn [-f] <command> [arg ...]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        // Strip --foreground / -f from the argument list.
        var foreground = false;
        var filteredArgs = new List<object?>();
        foreach (var arg in context.Arguments)
        {
            var s = arg?.ToString();
            if (s is "--foreground" or "-f")
            {
                foreground = true;
            }
            else
            {
                filteredArgs.Add(arg);
            }
        }

        if (filteredArgs.Count < 1)
        {
            throw context.CreateDiagnostic(
                code: "tosh.runtime.spawn_requires_command",
                title: "'spawn' requires an external command name or path.",
                label: "provide an external command to start");
        }

        var commandName = filteredArgs[0]?.ToString();
        if (string.IsNullOrWhiteSpace(commandName))
        {
            throw context.CreateDiagnostic(
                code: "tosh.runtime.spawn_requires_command",
                title: "'spawn' requires an external command name or path.",
                argumentIndex: 0,
                label: "this argument is empty");
        }

        var resolvedPath = ResolveExternalPath(context, commandName);
        var processArguments = filteredArgs.Skip(1).ToArray();

        if (foreground)
        {
            var external = new ExternalProcessCommand(commandName, resolvedPath);
            var forwardContext = context with { Arguments = processArguments };
            await foreach (var item in external.ExecuteAsync(forwardContext))
            {
                yield return item;
            }
            yield break;
        }

        var initialInput = await AsyncEnumerableExtensions.ToListAsync(
            ShellIterationUtilities.ReplaySingleInputCollectionAsync(context.Input, context.CancellationToken),
            context.CancellationToken);

        var commandText = BuildCommandText(commandName, processArguments);
        var job = RuntimeRegister(
            context,
            ShellJob.StartExternalProcess(
                context.Runtime.AllocateJobId(),
                commandText,
                resolvedPath,
                context.Runtime.CurrentDirectory,
                processArguments,
                initialInput));

        var info = job.ToInfo();
        context.Runtime.SetLastResult(info);
        context.Runtime.SetLastExitCode(0);
        yield return info;
    }

    private static ShellJob RuntimeRegister(CommandContext context, ShellJob job)
    {
        return context.Runtime.RegisterJob(job);
    }

    private static string ResolveExternalPath(CommandContext context, string commandName)
    {
        if (context.Runtime.Commands.TryGet(commandName, out var registered) && registered is ExternalProcessCommand external)
        {
            return external.ResolvedPath;
        }

        var lookup = ExternalCommandResolver.Resolve(context.Runtime.CurrentDirectory, commandName);
        return lookup.Status switch
        {
            ExternalCommandLookupStatus.Found when lookup.ResolvedPath is not null => lookup.ResolvedPath,
            ExternalCommandLookupStatus.NotExecutable => throw context.CreateDiagnostic(
                code: "tosh.runtime.spawn_target_not_executable",
                title: $"'{commandName}' exists but is not executable.",
                argumentIndex: 0,
                label: "make this file executable or use a different command"),
            ExternalCommandLookupStatus.IsDirectory => throw context.CreateDiagnostic(
                code: "tosh.runtime.spawn_target_is_directory",
                title: $"'{commandName}' resolves to a directory, not an executable.",
                argumentIndex: 0,
                label: "provide an executable file path or command name"),
            _ => throw context.CreateDiagnostic(
                code: "tosh.runtime.spawn_target_not_found",
                title: $"External command '{commandName}' was not found.",
                argumentIndex: 0,
                label: "this command is not on PATH and is not an executable path"),
        };
    }

    private static string BuildCommandText(string commandName, IReadOnlyList<object?> arguments)
    {
        var parts = new List<string> { commandName };

        foreach (var argument in arguments)
        {
            parts.Add(ExternalTextSerializer.SerializeArgument(argument));
        }

        return string.Join(' ', parts);
    }
}
