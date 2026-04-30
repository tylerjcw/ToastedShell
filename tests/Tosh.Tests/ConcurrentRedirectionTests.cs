using Tosh.Runtime;
using Tosh.Language;

namespace Tosh.Tests;

public sealed class ConcurrentRedirectionTests
{
    [Fact]
    public async Task Separate_stdout_and_stderr_redirections_to_different_files()
    {
        using var tempDirectory = new TemporaryDirectory();
        var stdoutFile = Path.Combine(tempDirectory.Path, "stdout.txt");
        var stderrFile = Path.Combine(tempDirectory.Path, "stderr.txt");
        var emitter = CreateScript(
            tempDirectory.Path,
            "emit-both",
            unixBody:
            """
            printf 'out1\nout2\n'
            printf 'err1\nerr2\n' >&2
            """,
            windowsBody:
            """
            @echo out1
            @echo out2
            @>&2 echo err1
            @>&2 echo err2
            """);

        var runtime = ToshRuntime.CreateDefault();
        runtime.CurrentDirectory = tempDirectory.Path;
        var engine = new ToshEngine(runtime);

        await engine.ExecuteToListAsync(
            $"./{emitter} out> \"{stdoutFile}\" err> \"{stderrFile}\"");

        var stdoutContent = (await File.ReadAllLinesAsync(stdoutFile)).Where(l => l.Length > 0).ToArray();
        var stderrContent = (await File.ReadAllLinesAsync(stderrFile)).Where(l => l.Length > 0).ToArray();

        Assert.Equal(["out1", "out2"], stdoutContent);
        Assert.Equal(["err1", "err2"], stderrContent);
    }

    [Fact]
    public async Task Combined_output_and_error_redirection_to_single_file()
    {
        using var tempDirectory = new TemporaryDirectory();
        var combinedFile = Path.Combine(tempDirectory.Path, "combined.txt");
        var emitter = CreateScript(
            tempDirectory.Path,
            "emit-both",
            unixBody:
            """
            printf 'out\n'
            printf 'err\n' >&2
            """,
            windowsBody:
            """
            @echo out
            @>&2 echo err
            """);

        var runtime = ToshRuntime.CreateDefault();
        runtime.CurrentDirectory = tempDirectory.Path;
        var engine = new ToshEngine(runtime);

        await engine.ExecuteToListAsync(
            $"./{emitter} o+e> \"{combinedFile}\"");

        var content = (await File.ReadAllLinesAsync(combinedFile)).Where(l => l.Length > 0).ToArray();

        // Combined redirect writes stdout first then stderr deterministically
        Assert.Equal(["out", "err"], content);
    }

    [Fact]
    public async Task Append_redirection_preserves_existing_content()
    {
        using var tempDirectory = new TemporaryDirectory();
        var file = Path.Combine(tempDirectory.Path, "output.txt");
        await File.WriteAllTextAsync(file, "existing\n");
        var emitter = CreateScript(
            tempDirectory.Path,
            "emit-appended",
            unixBody:
            """
            printf 'appended\n'
            """,
            windowsBody:
            """
            @echo appended
            """);

        var runtime = ToshRuntime.CreateDefault();
        runtime.CurrentDirectory = tempDirectory.Path;
        var engine = new ToshEngine(runtime);

        await engine.ExecuteToListAsync($"./{emitter} out>> \"{file}\"");

        var content = (await File.ReadAllLinesAsync(file)).Where(l => l.Length > 0).ToArray();
        Assert.Equal(["existing", "appended"], content);
    }

    [Fact]
    public async Task Sequential_redirections_to_same_file_do_not_corrupt()
    {
        using var tempDirectory = new TemporaryDirectory();
        var file = Path.Combine(tempDirectory.Path, "output.txt");
        var first = CreateScript(
            tempDirectory.Path,
            "emit-first",
            unixBody:
            """
            printf 'first\n'
            """,
            windowsBody:
            """
            @echo first
            """);
        var second = CreateScript(
            tempDirectory.Path,
            "emit-second",
            unixBody:
            """
            printf 'second\n'
            """,
            windowsBody:
            """
            @echo second
            """);
        var third = CreateScript(
            tempDirectory.Path,
            "emit-third",
            unixBody:
            """
            printf 'third\n'
            """,
            windowsBody:
            """
            @echo third
            """);

        var runtime = ToshRuntime.CreateDefault();
        runtime.CurrentDirectory = tempDirectory.Path;
        var engine = new ToshEngine(runtime);

        await engine.ExecuteToListAsync(
            $"""
            ./{first} out> "{file}"
            ./{second} out>> "{file}"
            ./{third} out>> "{file}"
            """);

        var content = (await File.ReadAllLinesAsync(file)).Where(l => l.Length > 0).ToArray();
        Assert.Equal(["first", "second", "third"], content);
    }

    [Fact]
    public async Task Redirection_to_nonexistent_path_produces_diagnostic()
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);

        var exception = await Assert.ThrowsAsync<ToshDiagnosticException>(
            () => engine.ExecuteToListAsync("echo hello out> /proc/nonexistent/impossible/file.txt"));

        Assert.Contains("redirection", exception.Diagnostics[0].Title, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Background_job_with_concurrent_stdout_and_stderr_redirections()
    {
        using var tempDirectory = new TemporaryDirectory();
        var stdoutFile = Path.Combine(tempDirectory.Path, "bg-stdout.txt");
        var stderrFile = Path.Combine(tempDirectory.Path, "bg-stderr.txt");
        var emitter = CreateScript(
            tempDirectory.Path,
            "emit-both",
            unixBody:
            """
            printf 'bg-out\n'
            printf 'bg-err\n' >&2
            """,
            windowsBody:
            """
            @echo bg-out
            @>&2 echo bg-err
            """);

        var runtime = ToshRuntime.CreateDefault();
        runtime.CurrentDirectory = tempDirectory.Path;
        var engine = new ToshEngine(runtime);

        await engine.ExecuteToListAsync(
            $"var so = \"{stdoutFile}\"\nvar se = \"{stderrFile}\"");
        await engine.ExecuteToListAsync(
            $"./{emitter} out> $so err> $se &");
        var startedInfo = Assert.IsType<ShellJobInfo>(runtime.LastResult);

        await engine.ExecuteToListAsync($"wait-for {startedInfo.Id}");

        var stdoutContent = (await File.ReadAllLinesAsync(stdoutFile)).Where(l => l.Length > 0).ToArray();
        var stderrContent = (await File.ReadAllLinesAsync(stderrFile)).Where(l => l.Length > 0).ToArray();

        Assert.Equal(["bg-out"], stdoutContent);
        Assert.Equal(["bg-err"], stderrContent);
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
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tosh-redir-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }
}
