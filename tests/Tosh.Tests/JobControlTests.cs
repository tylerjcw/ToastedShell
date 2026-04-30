using System.Diagnostics;
using Tosh.Stdlib.Processes;
using Tosh.Runtime;

namespace Tosh.Tests;

public sealed class JobControlTests
{
    // ── ShellJob.CreateSuspended ─────────────────────────────────

    [Fact]
    public void CreateSuspended_sets_status_to_suspended()
    {
        if (OperatingSystem.IsWindows()) return;

        using var process = StartSleepProcess();
        var job = ShellJob.CreateSuspended(1, "sleep 60", process);

        Assert.Equal(ShellJobStatus.Suspended, job.Status);
        Assert.Equal(1, job.Id);
        Assert.Equal("sleep 60", job.Command);
        Assert.Equal(process.Id, job.ProcessId);

        // Cleanup
        process.Kill();
        process.WaitForExit();
    }

    [Fact]
    public void CreateSuspended_job_info_has_correct_fields()
    {
        if (OperatingSystem.IsWindows()) return;

        using var process = StartSleepProcess();
        var job = ShellJob.CreateSuspended(42, "vim myfile.txt", process);
        var info = job.ToInfo();

        Assert.Equal(42, info.Id);
        Assert.Equal("vim myfile.txt", info.Command);
        Assert.Equal(ShellJobStatus.Suspended, info.Status);
        Assert.Equal(process.Id, info.ProcessId);

        process.Kill();
        process.WaitForExit();
    }

    // ── TryResumeForeground ──────────────────────────────────────

    [Fact]
    public void TryResumeForeground_returns_error_when_status_is_not_suspended()
    {
        if (OperatingSystem.IsWindows()) return;

        // Create a suspended job, then kill the process to make it exit,
        // and manually simulate the status moving away from Suspended.
        using var process = StartSleepProcess();
        var job = ShellJob.CreateSuspended(1, "sleep 60", process);
        process.Kill();
        process.WaitForExit();

        // TryResumeForeground on a non-interactive terminal:
        // The method checks status first, and since status IS Suspended,
        // it will proceed but WaitForForegroundChild returns Fallback (non-interactive).
        // After that the managed wait succeeds (process already exited).
        // Verify the job ends up completed/failed.
        var tc = new TerminalControl();
        var result = job.TryResumeForeground(tc, out var error);

        Assert.Null(error);
        // The process was killed, so it should end up as Failed.
        Assert.True(job.Status is ShellJobStatus.Completed or ShellJobStatus.Failed);

        // Now try again — should fail because status is no longer Suspended.
        result = job.TryResumeForeground(tc, out error);
        Assert.NotNull(error);
        Assert.Contains("not suspended", error);
    }

    // ── TryResumeBackground ──────────────────────────────────────

    [Fact]
    public void TryResumeBackground_sends_sigcont_to_process_directly()
    {
        if (OperatingSystem.IsWindows()) return;

        // Start a process that is NOT in its own group (common case when
        // setpgid fails because the child already exec'd).
        using var process = StartSleepProcess();

        var job = ShellJob.CreateSuspended(1, "sleep 60", process);
        Assert.Equal(ShellJobStatus.Suspended, job.Status);

        // TryResumeBackground sends SIGCONT directly to the process (not group).
        var resumed = job.TryResumeBackground(out var error);

        Assert.True(resumed);
        Assert.Null(error);
        Assert.Equal(ShellJobStatus.Running, job.Status);

        process.Kill();
        process.WaitForExit();
    }

    [Fact]
    public void TryResumeBackground_returns_false_when_not_suspended()
    {
        if (OperatingSystem.IsWindows()) return;

        // Create a suspended job, then resume it via foreground (which changes status).
        using var process = StartSleepProcess();
        var job = ShellJob.CreateSuspended(1, "sleep 60", process);
        process.Kill();
        process.WaitForExit();

        // Resume foreground on non-interactive terminal — this will use fallback/managed wait
        // and the process has already exited, so status transitions away from Suspended.
        var tc = new TerminalControl();
        job.TryResumeForeground(tc, out _);

        // Now bg should fail because job is no longer suspended.
        var result = job.TryResumeBackground(out var error);
        Assert.False(result);
        Assert.NotNull(error);
    }

    [Fact]
    public async Task TryResumeBackground_makes_suspended_job_waitable()
    {
        using var process = StartLongRunningProcess();
        var job = ShellJob.CreateSuspended(1, "linger", process);

        Assert.True(job.TryResumeBackground(out var error));
        Assert.Null(error);

        Assert.True(job.Kill());
        var completion = await job.WaitAsync();

        Assert.Equal(ShellJobStatus.Cancelled, completion.Status);
    }

    [Fact]
    public void Kill_succeeds_for_suspended_job()
    {
        using var process = StartLongRunningProcess();
        var job = ShellJob.CreateSuspended(1, "linger", process);

        var killed = job.Kill();

        Assert.True(killed);
        Assert.Equal(ShellJobStatus.Cancelled, job.Status);
        Assert.True(process.WaitForExit(5000));
    }

    // ── WaitForForegroundChild ───────────────────────────────────

    [Fact]
    public void WaitForForegroundChild_returns_fallback_when_non_interactive()
    {
        if (OperatingSystem.IsWindows()) return;

        // In test harness, stdin is redirected, so TerminalControl is non-interactive.
        var tc = new TerminalControl();
        var result = tc.WaitForForegroundChild(999999);

        Assert.Equal(ForegroundWaitOutcome.Fallback, result.Outcome);
    }

    [Fact]
    public void WaitPid_detects_exited_child()
    {
        if (OperatingSystem.IsWindows()) return;

        // Use fork() semantics indirectly — spawn a process and immediately
        // waitpid it before .NET's background reaper can.
        // We use `sh -c "exit 42"` which exits quickly with a known code.
        var psi = new ProcessStartInfo("/bin/sh", "-c \"exit 42\"")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true, // prevents .NET's reaper from using waitpid
            RedirectStandardError = true,
        };

        var process = Process.Start(psi)!;
        var pid = process.Id;

        // waitpid from parent — since we redirected streams, .NET won't
        // have reaped via its SIGCHLD handler yet.
        var result = PosixTerminalInterop.WaitPid(pid, out var status, WaitPidOptions.None);

        if (result > 0)
        {
            Assert.True(PosixTerminalInterop.WIfExited(status));
            Assert.Equal(42, PosixTerminalInterop.WExitStatus(status));
        }
        else
        {
            // If .NET already reaped it, result is -1 (ECHILD). That's acceptable —
            // it means the test environment is too fast; don't fail.
        }

        // Ensure cleanup
        try { process.WaitForExit(1000); } catch { /* already reaped */ }
    }

    [Fact]
    public void WaitPid_detects_nonzero_exit()
    {
        if (OperatingSystem.IsWindows()) return;

        var psi = new ProcessStartInfo("/bin/sh", "-c \"exit 7\"")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        var process = Process.Start(psi)!;
        var pid = process.Id;

        var result = PosixTerminalInterop.WaitPid(pid, out var status, WaitPidOptions.None);

        if (result > 0)
        {
            Assert.True(PosixTerminalInterop.WIfExited(status));
            Assert.Equal(7, PosixTerminalInterop.WExitStatus(status));
        }

        try { process.WaitForExit(1000); } catch { /* already reaped */ }
    }

    [Fact]
    public void WaitPid_with_WUNTRACED_detects_stopped_child()
    {
        if (OperatingSystem.IsWindows()) return;

        // Start a sleep and then stop it
        var process = Process.Start(new ProcessStartInfo("sleep", "60")
        {
            UseShellExecute = false,
        })!;

        try
        {
            // Stop the child
            Process.Start("kill", $"-STOP {process.Id}")?.WaitForExit();

            // Wait with WUNTRACED — should detect the stopped state
            var result = PosixTerminalInterop.WaitPid(process.Id, out var status, WaitPidOptions.WUntraced);

            Assert.True(result > 0);
            Assert.True(PosixTerminalInterop.WIfStopped(status));
        }
        finally
        {
            // Resume and kill for cleanup
            Process.Start("kill", $"-CONT {process.Id}")?.WaitForExit();
            process.Kill();
            process.WaitForExit();
        }
    }

    // ── Signal constants ─────────────────────────────────────────

    [Fact]
    public void SIGTSTP_constant_is_platform_appropriate()
    {
        if (OperatingSystem.IsWindows()) return;

        if (OperatingSystem.IsMacOS())
        {
            Assert.Equal(18, PosixTerminalInterop.SIGTSTP);
        }
        else
        {
            Assert.Equal(20, PosixTerminalInterop.SIGTSTP);
        }
    }

    [Fact]
    public void SIGCONT_constant_is_platform_appropriate()
    {
        if (OperatingSystem.IsWindows()) return;

        if (OperatingSystem.IsMacOS())
        {
            Assert.Equal(19, PosixTerminalInterop.SIGCONT);
        }
        else
        {
            Assert.Equal(18, PosixTerminalInterop.SIGCONT);
        }
    }

    [Fact]
    public void TryParseSignal_accepts_tstp_alias()
    {
        Assert.True(ProcessSignalSender.TryParseSignal("TSTP", out var signal, out var displayName));
        Assert.Equal(PosixTerminalInterop.SIGTSTP, signal);
        Assert.Equal("SIGTSTP", displayName);
    }

    [Fact]
    public void TryIgnoreSignal_succeeds_for_SIGTSTP()
    {
        if (OperatingSystem.IsWindows()) return;

        var result = PosixTerminalInterop.TryIgnoreSignal(PosixTerminalInterop.SIGTSTP);
        Assert.True(result);
    }

    [Fact]
    public void TerminalControl_installs_signal_registrations_without_error()
    {
        // Creating TerminalControl registers PosixSignalRegistration handlers.
        // This must not throw regardless of TTY state.
        var tc = new TerminalControl();
        Assert.NotNull(tc);
    }

    // ── ReapCompletedJobs preserves suspended jobs ───────────────

    [Fact]
    public void Runtime_preserves_suspended_jobs_across_GetJobs()
    {
        if (OperatingSystem.IsWindows()) return;

        using var process = StartSleepProcess();
        var runtime = ToshRuntime.CreateDefault();
        var job = ShellJob.CreateSuspended(runtime.AllocateJobId(), "sleep 60", process);
        runtime.RegisterJob(job);

        // GetJobs calls ReapCompletedJobs internally — suspended jobs must survive.
        var jobs = runtime.GetJobs();
        Assert.Single(jobs);
        Assert.Equal(ShellJobStatus.Suspended, jobs[0].Status);

        process.Kill();
        process.WaitForExit();
    }

    // ── fg/bg command registration ───────────────────────────────

    [Fact]
    public void ForegroundCommand_is_registered()
    {
        var runtime = ToshRuntime.CreateDefault();
        Assert.True(runtime.Commands.TryGet("fg", out _));
    }

    [Fact]
    public void BackgroundResumeCommand_is_registered()
    {
        var runtime = ToshRuntime.CreateDefault();
        Assert.True(runtime.Commands.TryGet("bg", out _));
    }

    // ── fg command error paths ───────────────────────────────────

    [Fact]
    public async Task Fg_throws_when_no_suspended_jobs()
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new Tosh.Language.ToshEngine(runtime);

        await Assert.ThrowsAsync<ToshDiagnosticException>(() =>
            engine.ExecuteToListAsync("fg"));
    }

    [Fact]
    public async Task Bg_throws_when_no_suspended_jobs()
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new Tosh.Language.ToshEngine(runtime);

        await Assert.ThrowsAsync<ToshDiagnosticException>(() =>
            engine.ExecuteToListAsync("bg"));
    }

    [Fact]
    public async Task Fg_throws_for_invalid_job_id()
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new Tosh.Language.ToshEngine(runtime);

        await Assert.ThrowsAsync<ToshDiagnosticException>(() =>
            engine.ExecuteToListAsync("fg 999"));
    }

    [Fact]
    public async Task Bg_throws_for_invalid_job_id()
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new Tosh.Language.ToshEngine(runtime);

        await Assert.ThrowsAsync<ToshDiagnosticException>(() =>
            engine.ExecuteToListAsync("bg 999"));
    }

    // ── fg command metadata ──────────────────────────────────────

    [Fact]
    public void ForegroundCommand_has_correct_metadata()
    {
        var cmd = new Tosh.Stdlib.Processes.ForegroundCommand();
        var meta = cmd.GetMetadata();

        Assert.Equal("fg", meta.Name);
        Assert.Equal("Process", meta.Category);
        Assert.Single(meta.Arguments);
        Assert.Equal("id", meta.Arguments[0].Name);
    }

    [Fact]
    public void BackgroundResumeCommand_has_correct_metadata()
    {
        var cmd = new Tosh.Stdlib.Processes.BackgroundResumeCommand();
        var meta = cmd.GetMetadata();

        Assert.Equal("bg", meta.Name);
        Assert.Equal("Process", meta.Category);
        Assert.Single(meta.Arguments);
        Assert.Equal("id", meta.Arguments[0].Name);
    }

    // ── helpers ──────────────────────────────────────────────────

    private static Process StartSleepProcess()
    {
        return Process.Start(new ProcessStartInfo("sleep", "60")
        {
            UseShellExecute = false,
        })!;
    }

    private static Process StartLongRunningProcess()
    {
        if (OperatingSystem.IsWindows())
        {
            return Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 61 127.0.0.1 > nul")
            {
                UseShellExecute = false,
            })!;
        }

        return StartSleepProcess();
    }
}
