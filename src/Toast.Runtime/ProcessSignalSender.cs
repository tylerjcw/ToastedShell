using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Tosh.Runtime;

public static class ProcessSignalSender
{
    private static readonly IReadOnlyDictionary<string, int> LinuxSignals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        ["HUP"] = 1,
        ["INT"] = 2,
        ["QUIT"] = 3,
        ["KILL"] = 9,
        ["USR1"] = 10,
        ["SEGV"] = 11,
        ["USR2"] = 12,
        ["PIPE"] = 13,
        ["ALRM"] = 14,
        ["TERM"] = 15,
        ["CONT"] = 18,
        ["STOP"] = 19,
        ["TSTP"] = 20,
    };

    private static readonly IReadOnlyDictionary<string, int> DarwinSignals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        ["HUP"] = 1,
        ["INT"] = 2,
        ["QUIT"] = 3,
        ["KILL"] = 9,
        ["USR1"] = 30,
        ["SEGV"] = 11,
        ["USR2"] = 31,
        ["PIPE"] = 13,
        ["ALRM"] = 14,
        ["TERM"] = 15,
        ["STOP"] = 17,
        ["CONT"] = 19,
        ["TSTP"] = 18,
    };

    public static bool TryParseSignal(object? value, out int signal, out string displayName)
    {
        switch (value)
        {
            case int intSignal when intSignal > 0:
                signal = intSignal;
                displayName = intSignal.ToString();
                return true;
            case long longSignal when longSignal is > 0 and <= int.MaxValue:
                signal = (int)longSignal;
                displayName = signal.ToString();
                return true;
            case string text:
                {
                    var trimmed = text.Trim();

                    if (int.TryParse(trimmed, out var parsedSignal) && parsedSignal > 0)
                    {
                        signal = parsedSignal;
                        displayName = parsedSignal.ToString();
                        return true;
                    }

                    if (trimmed.StartsWith("SIG", StringComparison.OrdinalIgnoreCase))
                    {
                        trimmed = trimmed[3..];
                    }

                    var table = OperatingSystem.IsMacOS() ? DarwinSignals : LinuxSignals;

                    if (table.TryGetValue(trimmed, out var resolved))
                    {
                        signal = resolved;
                        displayName = $"SIG{trimmed.ToUpperInvariant()}";
                        return true;
                    }

                    break;
                }
        }

        signal = 0;
        displayName = string.Empty;
        return false;
    }

    public static bool TrySend(int processId, int signal, out string? error)
    {
        if (OperatingSystem.IsWindows())
        {
            if (IsContinueSignal(signal))
            {
                return TryResumeWindowsProcess(processId, out error);
            }

            if (IsSuspendSignal(signal))
            {
                return TrySuspendWindowsProcess(processId, out error);
            }

            if (signal is 2 or 9 or 15)
            {
                try
                {
                    using var process = Process.GetProcessById(processId);
                    process.Kill(entireProcessTree: true);
                    error = null;
                    return true;
                }
                catch (Exception exception)
                {
                    error = exception.Message;
                    return false;
                }
            }

            error = "Windows currently supports INT, TERM, KILL, STOP, TSTP, and CONT semantics.";
            return false;
        }

        var result = Interop.kill(processId, signal);

        if (result == 0)
        {
            error = null;
            return true;
        }

        error = new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()).Message;
        return false;
    }

    internal static bool IsSuspendSignal(int signal)
    {
        var stopSignal = OperatingSystem.IsMacOS() ? 17 : 19;
        return signal == stopSignal || signal == PosixTerminalInterop.SIGTSTP;
    }

    internal static bool IsContinueSignal(int signal)
    {
        return signal == PosixTerminalInterop.SIGCONT;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static bool TrySuspendWindowsProcess(int processId, out string? error)
    {
        return TryControlWindowsProcess(processId, resume: false, out error);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static bool TryResumeWindowsProcess(int processId, out string? error)
    {
        return TryControlWindowsProcess(processId, resume: true, out error);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static bool TryControlWindowsProcess(int processId, bool resume, out string? error)
    {
        var desiredAccess = Interop.PROCESS_QUERY_LIMITED_INFORMATION | Interop.PROCESS_SUSPEND_RESUME;
        var processHandle = Interop.OpenProcess(desiredAccess, false, processId);

        if (processHandle == IntPtr.Zero)
        {
            error = new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()).Message;
            return false;
        }

        try
        {
            var status = resume
                ? Interop.NtResumeProcess(processHandle)
                : Interop.NtSuspendProcess(processHandle);

            if (status == 0)
            {
                error = null;
                return true;
            }

            error = $"NTSTATUS 0x{status:X8}";
            return false;
        }
        finally
        {
            Interop.CloseHandle(processHandle);
        }
    }

    /// <summary>
    /// Send a signal to an entire process group (kill(-pgid, signal)).
    /// On Windows, falls back to TrySend for the group leader PID.
    /// </summary>
    public static bool TrySendToGroup(int processGroupId, int signal, out string? error)
    {
        if (OperatingSystem.IsWindows())
        {
            return TrySend(processGroupId, signal, out error);
        }

        var result = Interop.kill(-processGroupId, signal);

        if (result == 0)
        {
            error = null;
            return true;
        }

        error = new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()).Message;
        return false;
    }

    private static class Interop
    {
        internal const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        internal const uint PROCESS_SUSPEND_RESUME = 0x0800;

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseHandle(IntPtr handle);

        [DllImport("ntdll.dll")]
        internal static extern int NtSuspendProcess(IntPtr processHandle);

        [DllImport("ntdll.dll")]
        internal static extern int NtResumeProcess(IntPtr processHandle);

        [DllImport("libc", SetLastError = true)]
        public static extern int kill(int pid, int sig);
    }
}
