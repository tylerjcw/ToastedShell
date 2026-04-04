using System.Diagnostics;
using Tosh.Core;
using Tosh.Language;

namespace Tosh.Tests;

public sealed class ExecCommandTests
{
    [Fact]
    public async Task Exec_passes_a_resolved_external_request_to_the_runtime_handler()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var tempDirectory = new TemporaryDirectory();
        var scriptPath = Path.Combine(tempDirectory.Path, "hello");
        await File.WriteAllTextAsync(scriptPath, "#!/usr/bin/env sh\nexit 0\n");
        File.SetUnixFileMode(scriptPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var runtime = ToshRuntime.CreateDefault();
        runtime.CurrentDirectory = tempDirectory.Path;
        var handler = new FakeExecHandler(new ShellExecResult(ReplacedCurrentProcess: true, ExitCode: 0));
        runtime.ExecHandler = handler;
        var engine = new ToshEngine(runtime);

        var results = await engine.ExecuteToListAsync("exec ./hello alpha beta");

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
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var tempDirectory = new TemporaryDirectory();
        var scriptPath = Path.Combine(tempDirectory.Path, "hello");
        await File.WriteAllTextAsync(scriptPath, "#!/usr/bin/env sh\nexit 0\n");
        File.SetUnixFileMode(scriptPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var runtime = ToshRuntime.CreateDefault();
        runtime.CurrentDirectory = tempDirectory.Path;
        runtime.ExecHandler = new FakeExecHandler(new ShellExecResult(ReplacedCurrentProcess: false, ExitCode: 7));
        var engine = new ToshEngine(runtime);

        var results = await engine.ExecuteToListAsync("exec ./hello");

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

        Assert.Equal("tosh::runtime::exec_pipeline_unsupported", diagnostic.Code);
    }

    [Fact]
    public async Task Cli_exec_replaces_the_process_and_preserves_the_child_exit_code()
    {
        if (!(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD()))
        {
            return;
        }

        var projectRoot = GetProjectRoot();
        var cliPath = Path.Combine(projectRoot, "src", "Tosh.Cli", "bin", "Debug", "net10.0", "Tosh.Cli.dll");
        using var configDirectory = new TemporaryDirectory();
        using var process = new Process();

        process.StartInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        process.StartInfo.Environment["TOSH_CONFIG_HOME"] = configDirectory.Path;
        process.StartInfo.ArgumentList.Add(cliPath);
        process.StartInfo.ArgumentList.Add("-c");
        process.StartInfo.ArgumentList.Add("exec /bin/sh -c \"exit 7\"");

        process.Start();
        await process.WaitForExitAsync();

        Assert.Equal(7, process.ExitCode);
    }

    private static string GetProjectRoot()
    {
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
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
