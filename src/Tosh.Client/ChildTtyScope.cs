using System.Runtime.InteropServices;

namespace Tosh.Client;

/// <summary>
/// Disposable scope that swaps the calling process's fd 0/1/2 to
/// <c>/dev/tty</c> and restores them on dispose.
///
/// Use this around spawning a sub-child that should drive the user's
/// terminal directly (e.g. an interactive builder, a pager, an editor).
/// Without it, in hybrid mode the child would inherit crumb's piped
/// stdout and its output would land in TōSh's TSSP parser as
/// unframed bytes.
///
/// No-op when both fd 1 and fd 2 are already TTYs, when <c>/dev/tty</c>
/// is unreachable, or on non-POSIX hosts.
/// </summary>
public sealed class ChildTtyScope : IDisposable
{
    [DllImport("libc", SetLastError = true)] private static extern int dup(int fd);
    [DllImport("libc", SetLastError = true)] private static extern int dup2(int oldfd, int newfd);
    [DllImport("libc", SetLastError = true)] private static extern int close(int fd);
    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int open_(string path, int flags);
    [DllImport("libc")] private static extern int isatty(int fd);

    private const int O_RDWR = 2;

    private int _savedIn = -1;
    private int _savedOut = -1;
    private int _savedErr = -1;
    private bool _disposed;

    internal ChildTtyScope() { }

    /// <summary>
    /// Acquire a scope that redirects fd 0/1/2 to <c>/dev/tty</c> for as
    /// long as the returned object is alive. Returns an empty
    /// (no-op) scope when redirection is unnecessary or impossible.
    /// </summary>
    public static ChildTtyScope Acquire()
    {
        var scope = new ChildTtyScope();
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS() && !OperatingSystem.IsFreeBSD())
        {
            return scope;
        }

        try
        {
            if (isatty(1) == 1 && isatty(2) == 1) return scope;
            if (!File.Exists("/dev/tty")) return scope;
            var tty = open_("/dev/tty", O_RDWR);
            if (tty < 0) return scope;

            scope._savedIn = dup(0);
            scope._savedOut = dup(1);
            scope._savedErr = dup(2);
            dup2(tty, 0);
            dup2(tty, 1);
            dup2(tty, 2);
            close(tty);
        }
        catch
        {
            // fall back to inherited fds.
        }

        return scope;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_savedIn >= 0) { dup2(_savedIn, 0); close(_savedIn); }
        if (_savedOut >= 0) { dup2(_savedOut, 1); close(_savedOut); }
        if (_savedErr >= 0) { dup2(_savedErr, 2); close(_savedErr); }
    }
}
