using System.Runtime.InteropServices;

namespace Tosh.Runtime;

/// <summary>
/// Low-level POSIX terminal and process-group interop via libc.
/// Unix-only; all public methods guard with OperatingSystem.IsWindows().
/// </summary>
public static class PosixTerminalInterop
{
    // ── process / session ────────────────────────────────────────────

    public static int GetProcessId() => Interop.getpid();

    public static int GetProcessGroupId() => Interop.getpgrp();

    public static int GetProcessGroupId(int pid) => Interop.getpgid(pid);

    /// <summary>
    /// Place <paramref name="pid"/> into process group <paramref name="pgid"/>.
    /// Pass pgid == 0 to use the pid as the new group leader.
    /// </summary>
    public static bool TrySetProcessGroupId(int pid, int pgid, out string? error)
    {
        if (Interop.setpgid(pid, pgid) == 0)
        {
            error = null;
            return true;
        }

        error = GetLastError();
        return false;
    }

    public static int CreateSession() => Interop.setsid();

    // ── terminal foreground group ────────────────────────────────────

    /// <summary>
    /// Returns the process group that currently owns the terminal on the given fd,
    /// or -1 on error.
    /// </summary>
    public static int GetTerminalForegroundGroup(int fd = StdinFd) => Interop.tcgetpgrp(fd);

    /// <summary>
    /// Transfers terminal foreground ownership to <paramref name="pgid"/>.
    /// </summary>
    public static bool TrySetTerminalForegroundGroup(int pgid, out string? error, int fd = StdinFd)
    {
        if (Interop.tcsetpgrp(fd, pgid) == 0)
        {
            error = null;
            return true;
        }

        error = GetLastError();
        return false;
    }

    // ── terminal attributes (termios) ────────────────────────────────

    public static bool TryGetTerminalAttributes(out Termios termios, out string? error, int fd = StdinFd)
    {
        termios = default;

        if (Interop.tcgetattr(fd, out termios) == 0)
        {
            error = null;
            return true;
        }

        error = GetLastError();
        return false;
    }

    public static bool TrySetTerminalAttributes(ref Termios termios, TermiosAction action, out string? error, int fd = StdinFd)
    {
        if (Interop.tcsetattr(fd, (int)action, ref termios) == 0)
        {
            error = null;
            return true;
        }

        error = GetLastError();
        return false;
    }

    // ── TTY detection ────────────────────────────────────────────────

    public static bool IsTerminal(int fd = StdinFd) => Interop.isatty(fd) == 1;

    // ── waitpid ──────────────────────────────────────────────────────

    /// <summary>
    /// Wait for a child process to change state.
    /// Returns the child PID on success, 0 if WNOHANG and no change, or -1 on error.
    /// </summary>
    public static int WaitPid(int pid, out int status, WaitPidOptions options)
    {
        return Interop.waitpid(pid, out status, (int)options);
    }

    /// <summary>True if the child exited normally.</summary>
    public static bool WIfExited(int status) => (status & 0x7F) == 0;

    /// <summary>Exit code (only valid when WIfExited is true).</summary>
    public static int WExitStatus(int status) => (status >> 8) & 0xFF;

    /// <summary>True if the child was stopped by a signal (e.g., SIGTSTP).</summary>
    public static bool WIfStopped(int status) => (status & 0xFF) == 0x7F;

    /// <summary>The signal that stopped the child (only valid when WIfStopped is true).</summary>
    public static int WStopSig(int status) => (status >> 8) & 0xFF;

    /// <summary>True if the child was terminated by a signal.</summary>
    public static bool WIfSignaled(int status) => !WIfExited(status) && !WIfStopped(status);

    /// <summary>The signal that killed the child (only valid when WIfSignaled is true).</summary>
    public static int WTermSig(int status) => status & 0x7F;

    // ── signal handling ──────────────────────────────────────────────

    /// <summary>
    /// Set a signal disposition to SIG_IGN (ignore).
    /// </summary>
    public static bool TryIgnoreSignal(int signal)
    {
        var sa = new SigAction { sa_handler = SigIgn };
        return Interop.sigaction(signal, ref sa, IntPtr.Zero) == 0;
    }

    /// <summary>
    /// Restore a signal to its default disposition (SIG_DFL).
    /// </summary>
    public static bool TryDefaultSignal(int signal)
    {
        var sa = new SigAction { sa_handler = SigDfl };
        return Interop.sigaction(signal, ref sa, IntPtr.Zero) == 0;
    }

    /// <summary>
    /// Read the current <c>sa_handler</c> for a signal without changing it.
    /// Returns the handler address, or <c>-1</c> on error.
    /// </summary>
    public static IntPtr GetCurrentSignalHandler(int signal)
    {
        var oldact = new SigAction { sa_mask = new byte[128] };
        if (Interop.sigaction_read(signal, IntPtr.Zero, ref oldact) == 0)
        {
            return oldact.sa_handler;
        }

        return new IntPtr(-1);
    }

    // Well-known signal numbers (Linux). macOS values differ for some;
    // TSTP/TTIN/TTOU/QUIT are the same on both.
    // Well-known signal numbers. Platform-specific values are resolved at init.
    public static readonly int SIGINT = 2;
    public static readonly int SIGQUIT = 3;
    public static readonly int SIGCONT = OperatingSystem.IsMacOS() ? 19 : 18;
    public static readonly int SIGTSTP = OperatingSystem.IsMacOS() ? 18 : 20;
    public static readonly int SIGTTIN = 21;
    public static readonly int SIGTTOU = 22;

    private static readonly IntPtr SigIgn = new(1);
    private static readonly IntPtr SigDfl = IntPtr.Zero;

    // ── constants ────────────────────────────────────────────────────

    public const int StdinFd = 0;
    public const int StdoutFd = 1;
    public const int StderrFd = 2;

    // ── helpers ──────────────────────────────────────────────────────

    private static string GetLastError()
    {
        return new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()).Message;
    }

    // ── P/Invoke ─────────────────────────────────────────────────────

    private static class Interop
    {
        [DllImport("libc", SetLastError = true)]
        public static extern int getpid();

        [DllImport("libc", SetLastError = true)]
        public static extern int getpgrp();

        [DllImport("libc", SetLastError = true)]
        public static extern int getpgid(int pid);

        [DllImport("libc", SetLastError = true)]
        public static extern int setpgid(int pid, int pgid);

        [DllImport("libc", SetLastError = true)]
        public static extern int setsid();

        [DllImport("libc", SetLastError = true)]
        public static extern int tcgetpgrp(int fd);

        [DllImport("libc", SetLastError = true)]
        public static extern int tcsetpgrp(int fd, int pgrp);

        [DllImport("libc", SetLastError = true)]
        public static extern int tcgetattr(int fd, out Termios termios);

        [DllImport("libc", SetLastError = true)]
        public static extern int tcsetattr(int fd, int optionalActions, ref Termios termios);

        [DllImport("libc", SetLastError = true)]
        public static extern int isatty(int fd);

        [DllImport("libc", SetLastError = true)]
        public static extern int waitpid(int pid, out int status, int options);

        [DllImport("libc", SetLastError = true)]
        public static extern int sigaction(int signum, ref SigAction act, IntPtr oldact);

        [DllImport("libc", SetLastError = true, EntryPoint = "sigaction")]
        public static extern int sigaction_read(int signum, IntPtr act, ref SigAction oldact);

        [DllImport("libc", SetLastError = true)]
        public static extern int sigemptyset(IntPtr set);

        [DllImport("libc", SetLastError = true)]
        public static extern int sigaddset(IntPtr set, int signum);

        [DllImport("libc", SetLastError = true)]
        public static extern int pthread_sigmask(int how, IntPtr set, IntPtr oldset);
    }

    // ── signal mask helpers ───────────────────────────────────────────

    private const int SIG_BLOCK = 0;
    private const int SIG_SETMASK = 2;
    // sigset_t is 128 bytes on Linux x86_64, 4 bytes on macOS.
    // Over-allocate to be safe on all platforms.
    private const int SigSetSize = 128;

    /// <summary>
    /// Block SIGTSTP, SIGTTIN, and SIGTTOU on the calling thread, returning
    /// the saved mask as an opaque handle.  Caller MUST later pass the handle
    /// to <see cref="RestoreSignalMask"/> to free the unmanaged allocation.
    /// </summary>
    public static bool BlockJobControlSignals(out IntPtr savedMask)
    {
        var set = Marshal.AllocHGlobal(SigSetSize);
        savedMask = Marshal.AllocHGlobal(SigSetSize);
        try
        {
            Interop.sigemptyset(set);
            Interop.sigaddset(set, SIGTSTP);
            Interop.sigaddset(set, SIGTTIN);
            Interop.sigaddset(set, SIGTTOU);
            return Interop.pthread_sigmask(SIG_BLOCK, set, savedMask) == 0;
        }
        finally
        {
            Marshal.FreeHGlobal(set);
        }
    }

    /// <summary>
    /// Restore the signal mask saved by <see cref="BlockJobControlSignals"/>.
    /// Frees the unmanaged memory.
    /// </summary>
    public static void RestoreSignalMask(IntPtr savedMask)
    {
        Interop.pthread_sigmask(SIG_SETMASK, savedMask, IntPtr.Zero);
        Marshal.FreeHGlobal(savedMask);
    }
}

/// <summary>waitpid option flags.</summary>
[Flags]
public enum WaitPidOptions
{
    None = 0,
    /// <summary>Also return if a child has stopped (WUNTRACED).</summary>
    WUntraced = 2,
    /// <summary>Return immediately if no child has changed state (WNOHANG).</summary>
    WNoHang = 1,
}

/// <summary>
/// Minimal <c>struct sigaction</c> for setting sa_handler only.
/// The full struct has platform-dependent size; we only need the handler
/// field and zero-initialize the rest.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct SigAction
{
    public IntPtr sa_handler;
    // sigset_t sa_mask — 128 bytes on Linux x86_64, pad to be safe.
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 128)]
    public byte[]? sa_mask;
    public int sa_flags;
    public IntPtr sa_restorer;
}

/// <summary>
/// Mirrors the POSIX <c>struct termios</c> layout.
/// The arrays are fixed-size to match the kernel ABI (Linux uses NCCS = 32).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct Termios
{
    public uint c_iflag;   // input modes
    public uint c_oflag;   // output modes
    public uint c_cflag;   // control modes
    public uint c_lflag;   // local modes

    public byte c_line;    // line discipline (Linux-specific, zero on macOS)

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
    public byte[] c_cc;    // control characters

    public uint c_ispeed;  // input baud rate
    public uint c_ospeed;  // output baud rate
}

/// <summary>
/// When to apply terminal attribute changes.
/// </summary>
public enum TermiosAction
{
    /// <summary>Apply immediately.</summary>
    Now = 0,        // TCSANOW

    /// <summary>Apply after all output has been transmitted.</summary>
    Drain = 1,      // TCSADRAIN

    /// <summary>Apply after output drained and discard pending input.</summary>
    Flush = 2,      // TCSAFLUSH
}
