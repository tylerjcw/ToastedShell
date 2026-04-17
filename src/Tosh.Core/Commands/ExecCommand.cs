namespace Tosh.Core.Commands;

[CommandCategory("Shell")]
[CommandArgument("command", "The external command to execute.")]
[CommandArgument("arg", "Arguments to pass to the command.", Required = false)]
[CommandOption("--", "Signals the end of options; everything after is treated as the command and arguments.")]
[CommandExample("exec tosh", Title = "Replace the current shell with a new tosh instance")]
[CommandExample("exec zsh", Title = "Switch to zsh")]
[CommandExample("exec /bin/sh -c \"echo hi\"", Title = "Exec a shell one-liner")]
[CommandNote("Exec replaces the current ToSh process with an external command. On Unix-like systems it uses native process replacement, so `exec tosh` or `exec zsh` behaves like the shell built-in you may know from zsh.")]
[CommandOutput("No output — the current process is replaced.")]
public sealed class ExecCommand : ShellCommand
{
    public ExecCommand()
        : base("exec", "Replace the current ToSh process with an external command.", "exec [--] <command> [arg ...]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (context.IsPipelined)
        {
            throw context.CreateDiagnostic(
                code: "tosh::runtime::exec_pipeline_unsupported",
                title: "`exec` only works as a standalone command.",
                label: "this `exec` runs inside a pipeline or receives pipeline input",
                help: "Use `exec <command>` by itself to replace the current ToSh process.");
        }

        var startIndex = 0;

        if (context.Arguments.Count > 0 &&
            context.Arguments[0] is string first &&
            string.Equals(first, "--", StringComparison.Ordinal))
        {
            startIndex = 1;
        }

        if (startIndex >= context.Arguments.Count)
        {
            throw context.CreateDiagnostic(
                code: "tosh::runtime::exec_missing_command",
                title: "`exec` needs a command to run.",
                label: "provide an external command after `exec`",
                help: "Examples: `exec tosh`, `exec zsh`, or `exec /bin/sh -c \"echo hi\"`.");
        }

        var target = SerializeTarget(context, context.Arguments[startIndex], startIndex);
        var lookup = ExternalCommandResolver.Resolve(context.Runtime.CurrentDirectory, target);
        var executablePath = ResolveExecutablePath(context, target, lookup, startIndex);
        var arguments = context.Arguments
            .Skip(startIndex + 1)
            .Select(ExternalTextSerializer.SerializeArgument)
            .ToArray();

        await context.Runtime.Output.FlushAsync(context.CancellationToken);
        await context.Runtime.Error.FlushAsync(context.CancellationToken);

        var result = await context.Runtime.ExecHandler.ExecuteAsync(
            new ShellExecRequest(
                ExecutablePath: executablePath,
                Arguments: arguments,
                WorkingDirectory: context.Runtime.CurrentDirectory),
            context.CancellationToken);

        context.Runtime.SetLastExitCode(result.ExitCode);
        context.PipelineExitStatusTracker?.Record(result.ExitCode);

        if (!result.ReplacedCurrentProcess)
        {
            context.Runtime.RequestExit();
        }

        yield break;
    }

    private static string SerializeTarget(CommandContext context, object? value, int argumentIndex)
    {
        var text = value switch
        {
            string stringValue => stringValue,
            FileSystemEntry entry => entry.FullName,
            FileSystemInfo fileInfo => fileInfo.FullName,
            _ => value?.ToString() ?? string.Empty,
        };

        if (string.IsNullOrWhiteSpace(text))
        {
            throw context.CreateDiagnostic(
                code: "tosh::runtime::exec_invalid_command",
                title: "`exec` needs a non-empty command name.",
                argumentIndex: argumentIndex,
                label: "this value does not name an external command",
                help: "Pass a command name, executable path, or path-like object.");
        }

        return text;
    }

    private static string ResolveExecutablePath(
        CommandContext context,
        string target,
        ExternalCommandLookupResult lookup,
        int argumentIndex)
    {
        return lookup.Status switch
        {
            ExternalCommandLookupStatus.Found when lookup.ResolvedPath is not null => lookup.ResolvedPath,
            ExternalCommandLookupStatus.NotExecutable => throw context.CreateDiagnostic(
                code: "tosh::runtime::external_command_not_executable",
                title: $"'{lookup.ResolvedPath ?? target}' is not executable.",
                argumentIndex: argumentIndex,
                label: $"'{target}' cannot be launched as a program",
                help: lookup.IsExplicitPath
                    ? $"make it executable, for example with `chmod +x {target}`, or run it with an interpreter."
                    : "check the file permissions or invoke it through an interpreter."),
            ExternalCommandLookupStatus.IsDirectory => throw context.CreateDiagnostic(
                code: "tosh::runtime::external_command_is_directory",
                title: $"'{lookup.ResolvedPath ?? target}' is a directory, not an executable file.",
                argumentIndex: argumentIndex,
                label: $"'{target}' resolved to a directory",
                help: "run an executable instead, or `cd` into the directory."),
            _ => throw context.CreateDiagnostic(
                code: "tosh::runtime::unknown_command",
                title: $"Command '{target}' was not found.",
                argumentIndex: argumentIndex,
                label: $"'{target}' is not a built-in, function, executable, or $-prefixed variable reference",
                help: $"use `which {target}` to inspect how Tosh resolves this command."),
        };
    }
}
