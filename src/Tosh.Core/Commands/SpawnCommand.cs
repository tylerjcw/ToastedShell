using Tosh.Core.Commands;

namespace Tosh.Core.Commands;

[CommandCategory("Concurrency")]
[CommandArgument("command", "External command name or path to start as a background job.")]
[CommandArgument("args", "Arguments to pass to the external command.", Required = false)]
[CommandExample("spawn dotnet --version", Title = "Start an external command in the background")]
[CommandExample("echo hello | spawn cat", Title = "Feed pipeline input into a background process")]
[CommandOutput("Returns a ShellJobInfo object for the started background job.")]
[PipelineInput(AcceptsScalar = true, AcceptsList = true, Description = "Optional pipeline input forwarded to the spawned process stdin.")]
[CommandNote("Use jobs to list active jobs and wait-for to await completion.")]
public sealed class SpawnCommand : ShellCommand
{
    public SpawnCommand()
        : base("spawn", "Starts an external command as a background job.", "spawn <command> [arg ...]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (context.Arguments.Count < 1)
        {
            throw context.CreateDiagnostic(
                code: "tosh::runtime::spawn_requires_command",
                title: "'spawn' requires an external command name or path.",
                label: "provide an external command to start");
        }

        var commandName = context.Arguments[0]?.ToString();
        if (string.IsNullOrWhiteSpace(commandName))
        {
            throw context.CreateDiagnostic(
                code: "tosh::runtime::spawn_requires_command",
                title: "'spawn' requires an external command name or path.",
                argumentIndex: 0,
                label: "this argument is empty");
        }

        var resolvedPath = ResolveExternalPath(context, commandName);
        var processArguments = context.Arguments.Skip(1).ToArray();
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
                code: "tosh::runtime::spawn_target_not_executable",
                title: $"'{commandName}' exists but is not executable.",
                argumentIndex: 0,
                label: "make this file executable or use a different command"),
            ExternalCommandLookupStatus.IsDirectory => throw context.CreateDiagnostic(
                code: "tosh::runtime::spawn_target_is_directory",
                title: $"'{commandName}' resolves to a directory, not an executable.",
                argumentIndex: 0,
                label: "provide an executable file path or command name"),
            _ => throw context.CreateDiagnostic(
                code: "tosh::runtime::spawn_target_not_found",
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
