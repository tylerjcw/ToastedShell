using System.Runtime.InteropServices;
using System.Text;

namespace Tosh.Client;

/// <summary>
/// Thin wrapper around <c>/dev/tty</c> for direct terminal I/O that works
/// across child-process boundaries. The lifetime model is one open per
/// call: every read drains stale input via <c>TCIFLUSH</c> before
/// blocking, and every write opens a fresh stream so output ordering
/// can't be skewed by a long-lived cached writer.
/// </summary>
internal static class TtyChannel
{
    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int LibcOpen(string path, int flags);
    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);
    [DllImport("libc", SetLastError = true)]
    private static extern nint read(int fd, byte[] buf, nuint count);
    [DllImport("libc", SetLastError = true)]
    private static extern nint write(int fd, byte[] buf, nuint count);
    [DllImport("libc", SetLastError = true)]
    private static extern int tcflush(int fd, int queueSelector);

    private const int O_RDONLY = 0;
    private const int O_WRONLY = 1;
    private const int TCIFLUSH = 0;

    public static bool IsAvailable
    {
        get
        {
            try { return File.Exists("/dev/tty"); }
            catch { return false; }
        }
    }

    /// <summary>
    /// Write a UTF-8 string straight to <c>/dev/tty</c>. Returns false
    /// when the tty is unreachable.
    /// </summary>
    public static bool TryWrite(string text)
    {
        if (string.IsNullOrEmpty(text)) return true;
        if (OperatingSystem.IsWindows()) return false;
        if (!IsAvailable) return false;
        var fd = LibcOpen("/dev/tty", O_WRONLY);
        if (fd < 0) return false;
        try
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            var remaining = bytes.Length;
            var offset = 0;
            while (remaining > 0)
            {
                var chunk = remaining > 4096 ? 4096 : remaining;
                var slice = offset == 0 && chunk == bytes.Length
                    ? bytes
                    : bytes.AsSpan(offset, chunk).ToArray();
                var n = write(fd, slice, (nuint)chunk);
                if (n <= 0) return false;
                offset += (int)n;
                remaining -= (int)n;
            }
            return true;
        }
        finally { close(fd); }
    }

    /// <summary>
    /// Read one line from <c>/dev/tty</c>. Opens a fresh fd, drains pending
    /// input via TCIFLUSH, then reads byte-by-byte until newline or EOF.
    /// Returns null when the tty is unreachable; returns "" on EOF after
    /// drain.
    /// </summary>
    public static string? TryReadLine()
    {
        if (OperatingSystem.IsWindows()) return null;
        if (!IsAvailable) return null;
        var fd = LibcOpen("/dev/tty", O_RDONLY);
        if (fd < 0) return null;
        try
        {
            tcflush(fd, TCIFLUSH);
            var sb = new StringBuilder();
            var buf = new byte[1];
            while (true)
            {
                var n = read(fd, buf, 1);
                if (n <= 0) return sb.Length == 0 ? null : sb.ToString();
                if (buf[0] == (byte)'\n') return sb.ToString();
                if (buf[0] == (byte)'\r') continue;
                sb.Append((char)buf[0]);
            }
        }
        finally { close(fd); }
    }
}
