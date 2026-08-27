using Tosh.Runtime;
using Tosh.Language;

namespace Tosh.Tests;

public sealed class JobHardeningTests
{
    [Fact]
    public async Task Completed_jobs_are_reaped_when_listing_jobs()
    {
        using var tempDirectory = new TemporaryDirectory();
        var commandName = CreateScript(
            tempDirectory.Path,
            "done",
            unixBody:
            """
            printf 'done\n'
            """,
            windowsBody:
            """
            @echo done
            """);

        var runtime = ToshRuntime.CreateDefault();
        runtime.CurrentDirectory = tempDirectory.Path;
        var engine = new ToshEngine(runtime.Language);

        // Start a fast background job
        await engine.ExecuteToListAsync("./" + commandName + " &");
        var startedInfo = Assert.IsType<ShellJobInfo>(runtime.LastResult);

        // Wait for it to finish
        await engine.ExecuteToListAsync($"wait-for {startedInfo.Id}");

        // The completed job should be reaped when listing jobs
        var listed = await engine.ExecuteToListAsync("jobs");
        Assert.DoesNotContain(listed, item => Assert.IsType<ShellJobInfo>(item).Id == startedInfo.Id);
    }

    [Fact]
    public async Task Completed_jobs_are_reaped_when_registering_new_jobs()
    {
        using var tempDirectory = new TemporaryDirectory();
        var firstCommand = CreateScript(
            tempDirectory.Path,
            "first",
            unixBody:
            """
            printf 'first\n'
            """,
            windowsBody:
            """
            @echo first
            """);
        var secondCommand = CreateScript(
            tempDirectory.Path,
            "second",
            unixBody:
            """
            printf 'second\n'
            """,
            windowsBody:
            """
            @echo second
            """);

        var runtime = ToshRuntime.CreateDefault();
        runtime.CurrentDirectory = tempDirectory.Path;
        var engine = new ToshEngine(runtime.Language);

        // Start and wait for a job to complete
        await engine.ExecuteToListAsync("./" + firstCommand + " &");
        var firstInfo = Assert.IsType<ShellJobInfo>(runtime.LastResult);
        await engine.ExecuteToListAsync($"wait-for {firstInfo.Id}");

        // Register a new background job — the completed one should be reaped
        await engine.ExecuteToListAsync("./" + secondCommand + " &");
        var secondInfo = Assert.IsType<ShellJobInfo>(runtime.LastResult);
        await engine.ExecuteToListAsync($"wait-for {secondInfo.Id}");

        // Only the second (now also completed) job might remain briefly, but the first must be gone
        var jobs = runtime.GetJobs();
        Assert.DoesNotContain(jobs, job => job.Id == firstInfo.Id);
    }

    [Fact]
    public async Task KillAllJobs_terminates_running_background_jobs()
    {
        using var tempDirectory = new TemporaryDirectory();
        var commandName = CreateScript(
            tempDirectory.Path,
            "linger",
            unixBody:
            """
            sleep 30
            """,
            windowsBody:
            """
            @ping -n 31 127.0.0.1 > nul
            """);

        var runtime = ToshRuntime.CreateDefault();
        runtime.CurrentDirectory = tempDirectory.Path;
        var engine = new ToshEngine(runtime.Language);

        // Start a long-running background job
        await engine.ExecuteToListAsync("./" + commandName + " &");
        var startedInfo = Assert.IsType<ShellJobInfo>(runtime.LastResult);
        Assert.True(runtime.TryGetJob(startedInfo.Id, out var job));
        Assert.Equal(ShellJobStatus.Running, job.Status);

        // Kill all jobs
        runtime.KillAllJobs();

        // Job registry should be empty
        Assert.Empty(runtime.GetJobs());
    }

    [Fact]
    public void KillAllJobs_on_empty_registry_is_harmless()
    {
        var runtime = ToshRuntime.CreateDefault();
        runtime.KillAllJobs();
        Assert.Empty(runtime.GetJobs());
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
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tosh-job-hardening-tests-{Guid.NewGuid():N}");
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
