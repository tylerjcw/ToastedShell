using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Tosh.Core;

/// <summary>
/// High-level terminal-ownership manager for launching external processes.
///
/// On Unix this saves/restores termios state, creates process groups for child
/// pipelines, and transfers terminal foreground control via tcsetpgrp.
///
/// On Windows (or when stdin is not a TTY) the methods are safe no-ops.
/// </summary>
public sealed class TerminalControl
{
    private Termios _savedTermios;
    private bool _hasSavedTermios;
    private readonly int _shellPgid;
    private readonly bool _isInteractive;

    // Keep signal registrations alive for the lifetime of the shell.
    // PosixSignalRegistration hooks into .NET's own signal chain, which
    // survives the runtime's lazy Console/terminal signal setup.
    private PosixSignalRegistration? _sigintRegistration;
    private PosixSignalRegistration? _sigtspRegistration;
    private PosixSignalRegistration? _sigquitRegistration;
    private PosixSignalRegistration? _sigttinRegistration;
    private PosixSignalRegistration? _sigttouRegistration;

    public TerminalControl()
    {
        if (OperatingSystem.IsWindows())
        {
            _isInteractive = false;
            return;
        }

        _isInteractive = PosixTerminalInterop.IsTerminal();

        if (!_isInteractive)
        {
            return;
        }

        _shellPgid = PosixTerminalInterop.GetProcessGroupId();
        SaveTerminalState();

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD())
        {
            InstallShellSignalHandlers();
        }
    }

    /// <summary>True when the shell owns a real TTY and terminal control is active.</summary>
    public bool IsInteractive => _isInteractive;

    /// <summary>The shell's own process-group ID (used to reclaim the terminal).</summary>
    public int ShellProcessGroupId => _shellPgid;

    // ── terminal state ───────────────────────────────────────────────

    /// <summary>
    /// Snapshot the current termios so we can restore it after an external
    /// process (which may have changed raw/cooked mode, echo, etc.).
    /// </summary>
    public void SaveTerminalState()
    {
        if (!_isInteractive)
        {
            return;
        }

        if (PosixTerminalInterop.TryGetTerminalAttributes(out var termios, out _))
        {
            _savedTermios = termios;
            _hasSavedTermios = true;
        }
    }

    /// <summary>
    /// Restore the terminal to the state captured by the last <see cref="SaveTerminalState"/>.
    /// Called after every foreground external process exits (or is suspended).
    /// </summary>
    public void RestoreTerminalState()
    {
        if (!_isInteractive || !_hasSavedTermios)
        {
            return;
        }

        var copy = _savedTermios;
        PosixTerminalInterop.TrySetTerminalAttributes(ref copy, TermiosAction.Now, out _);
    }

    // ── foreground group ─────────────────────────────────────────────

    /// <summary>
    /// Transfer terminal foreground ownership to the given process group.
    /// The child pipeline's leader PID is typically used as the group ID.
    /// </summary>
    public bool TrySetForegroundGroup(int pgid, out string? error)
    {
        if (!_isInteractive)
        {
            error = null;
            return true;
        }

        return PosixTerminalInterop.TrySetTerminalForegroundGroup(pgid, out error);
    }

    /// <summary>
    /// Reclaim terminal foreground ownership for the shell itself.
    /// </summary>
    public void ReclaimForeground()
    {
        if (!_isInteractive)
        {
            return;
        }

        PosixTerminalInterop.TrySetTerminalForegroundGroup(_shellPgid, out _);
    }

    // ── convenience: bracket a foreground child ──────────────────────

    /// <summary>
    /// Returns an <see cref="IDisposable"/> that transfers the terminal to
    /// <paramref name="childPgid"/> and restores it (plus termios) on dispose.
    /// Use with <c>using</c> around WaitForExitAsync.
    /// </summary>
    public ForegroundSession EnterForeground(int childPgid)
    {
        return new ForegroundSession(this, childPgid);
    }

    // ── waitpid-based foreground wait ────────────────────────────────

    /// <summary>
    /// Wait for a foreground child using <c>waitpid(WUNTRACED)</c> so we can
    /// detect both normal exit and suspension (Ctrl+Z / SIGTSTP).
    /// Returns a <see cref="ForegroundWaitResult"/> describing the outcome.
    /// Falls back to <see cref="System.Diagnostics.Process.WaitForExitAsync"/>
    /// on Windows or when stdin is not a TTY.
    /// </summary>
    public ForegroundWaitResult WaitForForegroundChild(int childPid)
    {
        if (!_isInteractive || OperatingSystem.IsWindows())
        {
            return ForegroundWaitResult.FallbackToManagedWait;
        }

        // Block SIGTSTP/SIGTTIN/SIGTTOU on this thread so the shell cannot
        // be suspended while waiting for the child.  The child was already
        // forked (.NET resets the child's mask during ForkAndExecProcess on
        // some runtimes, and on others the child inherits our mask but exec
        // does not change it — either way the child is a separate process
        // that receives signals independently).  After the loop we restore
        // the old mask; any pending signal is then delivered and handled by
        // the PosixSignalRegistration callbacks.
        PosixTerminalInterop.BlockJobControlSignals(out var savedMask);
        try
        {
            return WaitLoop(childPid);
        }
        finally
        {
            PosixTerminalInterop.RestoreSignalMask(savedMask);
        }
    }

    private static ForegroundWaitResult WaitLoop(int childPid)
    {
        while (true)
        {
            var result = PosixTerminalInterop.WaitPid(childPid, out var status, WaitPidOptions.WUntraced);

            if (result < 0)
            {
                // EINTR: a signal (e.g. our cancelled SIGTSTP) interrupted the
                // syscall — just retry.  Any other error (ECHILD) means the
                // child is gone; fall back to the managed wait path.
                if (Marshal.GetLastWin32Error() == 4) // EINTR
                {
                    continue;
                }

                return ForegroundWaitResult.FallbackToManagedWait;
            }

            if (PosixTerminalInterop.WIfExited(status))
            {
                return new ForegroundWaitResult(ForegroundWaitOutcome.Exited, PosixTerminalInterop.WExitStatus(status));
            }

            if (PosixTerminalInterop.WIfSignaled(status))
            {
                // Killed by a signal — treat as exit code 128 + signal (POSIX convention).
                return new ForegroundWaitResult(ForegroundWaitOutcome.Exited, 128 + PosixTerminalInterop.WTermSig(status));
            }

            if (PosixTerminalInterop.WIfStopped(status))
            {
                return new ForegroundWaitResult(ForegroundWaitOutcome.Stopped, PosixTerminalInterop.WStopSig(status));
            }

            // Continued or other status — keep waiting.
        }
    }

    // ── shell signal discipline ──────────────────────────────────────

    /// <summary>
    /// Make the shell ignore job-control signals that should only affect children.
    /// This matches the behavior of bash, fish, and nushell.
    ///
    /// We use <see cref="PosixSignalRegistration"/> rather than raw sigaction
    /// because .NET's Console subsystem installs its own signal handlers
    /// (during the first ReadKey/ReadLine call) which would overwrite any
    /// disposition set through direct sigaction P/Invoke.
    /// PosixSignalRegistration hooks into the runtime's signal chain and
    /// survives that lazy initialization.
    ///
    /// Bonus: children forked from a PosixSignalRegistration handler inherit
    /// SIG_DFL (not SIG_IGN), so they remain suspendable — which is the
    /// correct behavior for child processes.
    /// </summary>
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    [SupportedOSPlatform("freebsd")]
    private void InstallShellSignalHandlers()
    {
        // Use PosixSignalRegistration which hooks into .NET's signal chain.
        // Children inherit SIG_DFL (not SIG_IGN) for caught signals, making
        // them properly suspendable/interruptible — correct for job control.
        //
        // SIGINT: prevent Ctrl+C from killing the shell when a foreground
        // child shares the shell's process group.  The child still receives
        // SIGINT from the kernel (it inherits SIG_DFL after exec).
        _sigintRegistration = PosixSignalRegistration.Create(
            PosixSignal.SIGINT, static ctx => ctx.Cancel = true);
        _sigtspRegistration = PosixSignalRegistration.Create(
            PosixSignal.SIGTSTP, static ctx => ctx.Cancel = true);
        _sigquitRegistration = PosixSignalRegistration.Create(
            PosixSignal.SIGQUIT, static ctx => ctx.Cancel = true);
        _sigttinRegistration = PosixSignalRegistration.Create(
            PosixSignal.SIGTTIN, static ctx => ctx.Cancel = true);
        _sigttouRegistration = PosixSignalRegistration.Create(
            PosixSignal.SIGTTOU, static ctx => ctx.Cancel = true);
    }

    public readonly struct ForegroundSession : IDisposable
    {
        private readonly TerminalControl _owner;

        internal ForegroundSession(TerminalControl owner, int childPgid)
        {
            _owner = owner;
            _owner.TrySetForegroundGroup(childPgid, out _);
        }

        public void Dispose()
        {
            _owner.ReclaimForeground();
            _owner.RestoreTerminalState();
        }
    }
}

public enum ForegroundWaitOutcome
{
    /// <summary>Child exited (normally or via signal).</summary>
    Exited,

    /// <summary>Child was stopped by a signal (e.g. SIGTSTP from Ctrl+Z).</summary>
    Stopped,

    /// <summary>Couldn't use waitpid; caller should use managed wait.</summary>
    Fallback,
}

public readonly struct ForegroundWaitResult
{
    public ForegroundWaitOutcome Outcome { get; }
    public int StatusOrSignal { get; }

    public ForegroundWaitResult(ForegroundWaitOutcome outcome, int statusOrSignal)
    {
        Outcome = outcome;
        StatusOrSignal = statusOrSignal;
    }

    public static ForegroundWaitResult FallbackToManagedWait { get; } =
        new(ForegroundWaitOutcome.Fallback, 0);
}
