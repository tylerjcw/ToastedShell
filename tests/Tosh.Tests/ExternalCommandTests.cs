using Tosh.Core;
using Tosh.Language;

namespace Tosh.Tests;

public sealed class ExternalCommandTests
{
    [Fact]
    public async Task Can_execute_external_command_by_explicit_relative_path()
    {
        using var tempDirectory = new TemporaryDirectory();
        var commandName = CreateScript(
            tempDirectory.Path,
            "hello",
            unixBody:
            """
            printf 'alpha\nbeta\n'
            """,
            windowsBody:
            """
            @echo alpha
            @echo beta
            """);

        var runtime = ToshRuntime.CreateDefault();
        runtime.CurrentDirectory = tempDirectory.Path;
        var engine = new ToshEngine(runtime);

        var results = await engine.ExecuteToListAsync("./" + commandName);

        Assert.Equal(["alpha", "beta"], results.Select(item => item?.ToString()).ToArray());
        Assert.Equal(0, runtime.LastExitCode);
    }

    [Fact]
    public async Task Can_execute_external_command_from_path()
    {
        using var tempDirectory = new TemporaryDirectory();
        CreateScript(
            tempDirectory.Path,
            "path-hello",
            unixBody:
            """
            printf 'from-path\n'
            """,
            windowsBody:
            """
            @echo from-path
            """);

        using var _ = new TemporaryPathScope(tempDirectory.Path);
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);

        var results = await engine.ExecuteToListAsync("path-hello");

        Assert.Equal(["from-path"], results.Select(item => item?.ToString()).ToArray());
        Assert.Equal(0, runtime.LastExitCode);
    }

    [Fact]
    public async Task External_commands_receive_pipeline_input_on_stdin()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var tempDirectory = new TemporaryDirectory();
        var commandName = CreateScript(
            tempDirectory.Path,
            "stdin-copy",
            unixBody:
            """
            cat
            """,
            windowsBody:
            """
            @more
            """);

        var runtime = ToshRuntime.CreateDefault();
        runtime.CurrentDirectory = tempDirectory.Path;
        var engine = new ToshEngine(runtime);

        var results = await engine.ExecuteToListAsync("echo alpha beta | ./" + commandName);

        Assert.Equal(["alpha", "beta"], results.Select(item => item?.ToString()).ToArray());
        Assert.Equal(0, runtime.LastExitCode);
    }

    [Fact]
    public async Task External_commands_require_executable_permissions()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var tempDirectory = new TemporaryDirectory();
        var commandPath = Path.Combine(tempDirectory.Path, "not-executable");
        await File.WriteAllTextAsync(commandPath, "#!/usr/bin/env sh\nprintf 'nope\\n'\n");
        File.SetUnixFileMode(commandPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        var runtime = ToshRuntime.CreateDefault();
        runtime.CurrentDirectory = tempDirectory.Path;
        var engine = new ToshEngine(runtime);

        var exception = await Assert.ThrowsAsync<ToshDiagnosticException>(async () => await engine.ExecuteToListAsync("./not-executable"));
        var diagnostic = Assert.Single(exception.Diagnostics);

        Assert.Equal("tosh::runtime::external_command_not_executable", diagnostic.Code);
    }

    [Fact]
    public async Task Which_only_returns_executable_external_commands()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var tempDirectory = new TemporaryDirectory();
        CreateScript(
            tempDirectory.Path,
            "visible-command",
            unixBody:
            """
            printf 'ok\n'
            """,
            windowsBody:
            """
            @echo ok
            """);

        var hiddenCommandPath = Path.Combine(tempDirectory.Path, "hidden-command");
        await File.WriteAllTextAsync(hiddenCommandPath, "#!/usr/bin/env sh\nprintf 'hidden\\n'\n");
        File.SetUnixFileMode(hiddenCommandPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        using var _ = new TemporaryPathScope(tempDirectory.Path);
        var engine = new ToshEngine();

        var visibleResults = await engine.ExecuteToListAsync("which visible-command");
        var hiddenResults = await engine.ExecuteToListAsync("which hidden-command");

        Assert.Contains(visibleResults, item => Assert.IsType<CommandResolution>(item).Kind == CommandResolutionKind.External);
        Assert.Empty(hiddenResults);
    }

    [Fact]
    public async Task External_commands_update_last_exit_code_when_they_fail()
    {
        using var tempDirectory = new TemporaryDirectory();
        var commandName = CreateScript(
            tempDirectory.Path,
            "fail-status",
            unixBody:
            """
            exit 7
            """,
            windowsBody:
            """
            @exit /b 7
            """);

        var runtime = ToshRuntime.CreateDefault();
        runtime.CurrentDirectory = tempDirectory.Path;
        var engine = new ToshEngine(runtime);

        var results = await engine.ExecuteToListAsync("./" + commandName);

        Assert.Empty(results);
        Assert.Equal(7, runtime.LastExitCode);
        Assert.Equal(7, Assert.IsType<int>(runtime.Variables["LastExitCode"]));
    }

    [Fact]
    public async Task External_text_lines_behave_like_strings_in_pipelines()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);

        var equalityResults = await engine.ExecuteToListAsync("/bin/echo hello | where $it == \"hello\"");
        var methodResults = await engine.ExecuteToListAsync("/bin/echo hello | each { $it.ToUpper() }");
        var memberResults = await engine.ExecuteToListAsync("/bin/echo hello | get Length");

        Assert.Single(equalityResults);
        Assert.Equal(["HELLO"], methodResults);
        Assert.Equal([5], memberResults);
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

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tosh-external-tests-{Guid.NewGuid():N}");
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

    private sealed class TemporaryPathScope : IDisposable
    {
        private readonly string? _previousPath;

        public TemporaryPathScope(string prependDirectory)
        {
            _previousPath = Environment.GetEnvironmentVariable("PATH");
            var updatedPath = string.IsNullOrWhiteSpace(_previousPath)
                ? prependDirectory
                : prependDirectory + Path.PathSeparator + _previousPath;
            Environment.SetEnvironmentVariable("PATH", updatedPath);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("PATH", _previousPath);
        }
    }
}
