using System.Diagnostics;
using Tosh.Runtime;
using Tosh.Language;

namespace Tosh.Tests;

public sealed class ExecCommandTests
{
    [Fact]
    public async Task Exec_passes_a_resolved_external_request_to_the_runtime_handler()
    {
        using var tempDirectory = new TemporaryDirectory();
        var commandName = CreateScript(
            tempDirectory.Path,
            "hello",
            unixBody:
            """
            exit 0
            """,
            windowsBody:
            """
            @exit /b 0
            """);
        var scriptPath = Path.Combine(tempDirectory.Path, commandName);

        var runtime = ToshRuntime.CreateDefault();
        runtime.CurrentDirectory = tempDirectory.Path;
        var handler = new FakeExecHandler(new ShellExecResult(ReplacedCurrentProcess: true, ExitCode: 0));
        runtime.ExecHandler = handler;
        var engine = new ToshEngine(runtime);

        var results = await engine.ExecuteToListAsync("exec ./" + commandName + " alpha beta");

        Assert.Empty(results);
        Assert.NotNull(handler.Request);
        Assert.Equal(scriptPath, handler.Request!.ExecutablePath);
        Assert.Equal(["alpha", "beta"], handler.Request.Arguments);
        Assert.Equal(tempDirectory.Path, handler.Request.WorkingDirectory);
        Assert.False(runtime.ExitRequested);
        Assert.Equal(0, runtime.LastExitCode);
    }

    [Fact]
    public async Task Exec_requests_shell_exit_when_fallback_execution_returns()
    {
        using var tempDirectory = new TemporaryDirectory();
        var commandName = CreateScript(
            tempDirectory.Path,
            "hello",
            unixBody:
            """
            exit 0
            """,
            windowsBody:
            """
            @exit /b 0
            """);

        var runtime = ToshRuntime.CreateDefault();
        runtime.CurrentDirectory = tempDirectory.Path;
        runtime.ExecHandler = new FakeExecHandler(new ShellExecResult(ReplacedCurrentProcess: false, ExitCode: 7));
        var engine = new ToshEngine(runtime);

        var results = await engine.ExecuteToListAsync("exec ./" + commandName);

        Assert.Empty(results);
        Assert.True(runtime.ExitRequested);
        Assert.Equal(7, runtime.LastExitCode);
    }

    [Fact]
    public async Task Exec_rejects_pipeline_usage()
    {
        var runtime = ToshRuntime.CreateDefault();
        runtime.ExecHandler = new FakeExecHandler(new ShellExecResult(ReplacedCurrentProcess: true, ExitCode: 0));
        var engine = new ToshEngine(runtime);

        var exception = await Assert.ThrowsAsync<ToshDiagnosticException>(async () => await engine.ExecuteToListAsync("echo hello | exec /bin/sh"));
        var diagnostic = Assert.Single(exception.Diagnostics);

        Assert.Equal("tosh.runtime.exec_pipeline_unsupported", diagnostic.Code);
    }

    [Fact]
    public async Task Cli_exec_replaces_the_process_and_preserves_the_child_exit_code()
    {
        var cliPath = GetCliPath();
        using var tempDirectory = new TemporaryDirectory();
        var commandName = CreateScript(
            tempDirectory.Path,
            "exit7",
            unixBody:
            """
            exit 7
            """,
            windowsBody:
            """
            @exit /b 7
            """);
        using var configDirectory = new TemporaryDirectory();
        using var stateDirectory = new TemporaryDirectory();
        using var process = new Process();

        process.StartInfo = new ProcessStartInfo
        {
            FileName = cliPath,
            WorkingDirectory = tempDirectory.Path,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        process.StartInfo.Environment["TOSH_CONFIG_HOME"] = configDirectory.Path;
        process.StartInfo.Environment["TOSH_STATE_HOME"] = stateDirectory.Path;
        process.StartInfo.ArgumentList.Add("-c");
        process.StartInfo.ArgumentList.Add("exec ./" + commandName);

        process.Start();
        await process.WaitForExitAsync();

        Assert.Equal(7, process.ExitCode);
    }

    [Fact]
    public async Task Cli_command_mode_uses_process_working_directory_even_with_saved_directory_stack_state()
    {
        var cliPath = GetCliPath();
        using var workingDirectory = new TemporaryDirectory();
        using var previousDirectory = new TemporaryDirectory();
        using var configDirectory = new TemporaryDirectory();
        using var stateDirectory = new TemporaryDirectory();
        using var process = new Process();
        var statePath = Path.Combine(stateDirectory.Path, "dirstack.json");

        await File.WriteAllTextAsync(
            statePath,
            $$"""
            {
              "entries": [
                "{{previousDirectory.Path.Replace("\\", "\\\\", StringComparison.Ordinal)}}"
              ],
              "index": 0
            }
            """);

        process.StartInfo = new ProcessStartInfo
        {
            FileName = cliPath,
            WorkingDirectory = workingDirectory.Path,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        process.StartInfo.Environment["TOSH_CONFIG_HOME"] = configDirectory.Path;
        process.StartInfo.Environment["TOSH_STATE_HOME"] = stateDirectory.Path;
        process.StartInfo.ArgumentList.Add("-c");
        process.StartInfo.ArgumentList.Add("pwd | get FullName");

        process.Start();
        var stdout = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.Equal(0, process.ExitCode);
        Assert.Contains(workingDirectory.Path, stdout, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private static string GetCliPath()
    {
        var projectRoot = GetProjectRoot();
        var cliName = OperatingSystem.IsWindows() ? "Tosh.Cli.exe" : "Tosh.Cli";
        return Path.Combine(projectRoot, "src", "Tosh.Cli", "bin", "Debug", "net10.0", cliName);
    }

    private static string GetProjectRoot()
    {
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
    }

    private static string CreateScript(string directory, string name, string unixBody, string windowsBody)
    {
        if (OperatingSystem.IsWindows())
        {
            var path = Path.Combine(directory, name + ".cmd");
            File.WriteAllText(path, $"@echo off{Environment.NewLine}{windowsBody.Trim().Replace("\n", Environment.NewLine, StringComparison.Ordinal)}{Environment.NewLine}");
            return Path.GetFileName(path);
        }

        var scriptPath = Path.Combine(directory, name);
        File.WriteAllText(scriptPath, $"#!/usr/bin/env sh\n{unixBody.Trim()}\n");
        File.SetUnixFileMode(
            scriptPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        return Path.GetFileName(scriptPath);
    }

    private sealed class FakeExecHandler : IShellExecHandler
    {
        private readonly ShellExecResult _result;

        public FakeExecHandler(ShellExecResult result)
        {
            _result = result;
        }

        public ShellExecRequest? Request { get; private set; }

        public Task<ShellExecResult> ExecuteAsync(ShellExecRequest request, CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(_result);
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tosh-exec-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
