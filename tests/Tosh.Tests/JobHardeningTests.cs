using Tosh.Core;
using Tosh.Language;

namespace Tosh.Tests;

public sealed class JobHardeningTests
{
    [Fact]
    public async Task Completed_jobs_are_reaped_when_listing_jobs()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);

        // Start a fast background job
        await engine.ExecuteToListAsync("/bin/sh -c \"printf 'done\\n'\" &");
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
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);

        // Start and wait for a job to complete
        await engine.ExecuteToListAsync("/bin/sh -c \"printf 'first\\n'\" &");
        var firstInfo = Assert.IsType<ShellJobInfo>(runtime.LastResult);
        await engine.ExecuteToListAsync($"wait-for {firstInfo.Id}");

        // Register a new background job — the completed one should be reaped
        await engine.ExecuteToListAsync("/bin/sh -c \"printf 'second\\n'\" &");
        var secondInfo = Assert.IsType<ShellJobInfo>(runtime.LastResult);
        await engine.ExecuteToListAsync($"wait-for {secondInfo.Id}");

        // Only the second (now also completed) job might remain briefly, but the first must be gone
        var jobs = runtime.GetJobs();
        Assert.DoesNotContain(jobs, job => job.Id == firstInfo.Id);
    }

    [Fact]
    public async Task KillAllJobs_terminates_running_background_jobs()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);

        // Start a long-running background job
        await engine.ExecuteToListAsync("/bin/sh -c \"sleep 30\" &");
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
}
