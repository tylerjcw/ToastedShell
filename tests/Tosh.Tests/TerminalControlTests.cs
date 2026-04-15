using Tosh.Core;

namespace Tosh.Tests;

public sealed class TerminalControlTests
{
    [Fact]
    public void TerminalControl_can_be_created_without_tty()
    {
        // In CI / test environments stdin is typically not a TTY.
        // TerminalControl must not throw — it just becomes a no-op.
        var tc = new TerminalControl();
        Assert.NotNull(tc);
    }

    [Fact]
    public void TerminalControl_reports_non_interactive_when_no_tty()
    {
        var tc = new TerminalControl();

        // Test harnesses redirect stdin, so this should be false.
        // On a real TTY this test would still pass (true is fine).
        // The key point: it doesn't throw.
        _ = tc.IsInteractive;
    }

    [Fact]
    public void SaveTerminalState_is_safe_when_non_interactive()
    {
        var tc = new TerminalControl();
        tc.SaveTerminalState(); // must not throw
    }

    [Fact]
    public void RestoreTerminalState_is_safe_when_non_interactive()
    {
        var tc = new TerminalControl();
        tc.RestoreTerminalState(); // must not throw
    }

    [Fact]
    public void ReclaimForeground_is_safe_when_non_interactive()
    {
        var tc = new TerminalControl();
        tc.ReclaimForeground(); // must not throw
    }

    [Fact]
    public void EnterForeground_returns_disposable_when_non_interactive()
    {
        var tc = new TerminalControl();

        using var session = tc.EnterForeground(12345);
        // Dispose must not throw
    }

    [Fact]
    public void TrySetForegroundGroup_is_safe_regardless_of_tty()
    {
        var tc = new TerminalControl();

        // When non-interactive: returns true (no-op).
        // When interactive: tcsetpgrp(12345) will fail, but must not throw.
        var result = tc.TrySetForegroundGroup(12345, out var error);

        if (!tc.IsInteractive)
        {
            Assert.True(result);
            Assert.Null(error);
        }
        else
        {
            // Interactive — 12345 is not a valid pgid, so the call fails gracefully.
            Assert.False(result);
        }
    }

    [Fact]
    public void PosixTerminalInterop_IsTerminal_works_for_stdin()
    {
        if (OperatingSystem.IsWindows()) return;

        // isatty(0) returns what the OS says — it must not crash.
        // In CI/redirected stdin: false. In an interactive terminal: true.
        _ = PosixTerminalInterop.IsTerminal(PosixTerminalInterop.StdinFd);
    }

    [Fact]
    public void PosixTerminalInterop_GetProcessId_returns_positive()
    {
        if (OperatingSystem.IsWindows()) return;

        var pid = PosixTerminalInterop.GetProcessId();
        Assert.True(pid > 0);
    }

    [Fact]
    public void PosixTerminalInterop_GetProcessGroupId_returns_positive()
    {
        if (OperatingSystem.IsWindows()) return;

        var pgid = PosixTerminalInterop.GetProcessGroupId();
        Assert.True(pgid > 0);
    }

    [Fact]
    public void PosixTerminalInterop_GetProcessGroupId_for_self_matches_getpgrp()
    {
        if (OperatingSystem.IsWindows()) return;

        var pid = PosixTerminalInterop.GetProcessId();
        var selfPgid = PosixTerminalInterop.GetProcessGroupId(pid);
        var pgid = PosixTerminalInterop.GetProcessGroupId();

        Assert.Equal(pgid, selfPgid);
    }

    [Fact]
    public void PosixTerminalInterop_TryGetTerminalAttributes_does_not_crash()
    {
        if (OperatingSystem.IsWindows()) return;

        // In test harness with redirected stdin, tcgetattr may fail, but must not crash.
        var result = PosixTerminalInterop.TryGetTerminalAttributes(out _, out var error);
        _ = result;
    }

    [Fact]
    public void ProcessSignalSender_TrySendToGroup_fails_for_nonexistent_group()
    {
        if (OperatingSystem.IsWindows()) return;

        // PID 999999 almost certainly doesn't exist as a process group.
        var result = ProcessSignalSender.TrySendToGroup(999999, 0, out var error);

        // Signal 0 is a no-op check; should fail with ESRCH for nonexistent group.
        Assert.False(result);
        Assert.NotNull(error);
    }

    [Fact]
    public async Task External_command_passthrough_still_works_after_terminal_control()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var tempDirectory = new TemporaryDirectory();
        var scriptPath = Path.Combine(tempDirectory.Path, "hello.sh");
        await File.WriteAllTextAsync(scriptPath,
            """
            #!/usr/bin/env sh
            printf 'terminal-ok\n'
            """);
        File.SetUnixFileMode(scriptPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var runtime = ToshRuntime.CreateDefault();
        runtime.CurrentDirectory = tempDirectory.Path;
        var engine = new Tosh.Language.ToshEngine(runtime);

        // When piped, output is captured — this exercises the piped path
        // which should still work unchanged.
        var results = await engine.ExecuteToListAsync("echo test | ./hello.sh");

        Assert.Equal(["terminal-ok"], results.Select(item => item?.ToString()!).ToArray());
        Assert.Equal(0, runtime.LastExitCode);
    }

    [Fact]
    public void ToshRuntime_exposes_terminal_control()
    {
        var runtime = ToshRuntime.CreateDefault();
        Assert.NotNull(runtime.Terminal);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tosh-terminal-tests-{Guid.NewGuid():N}");
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
