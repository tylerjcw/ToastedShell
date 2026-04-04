using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Tosh.Core;

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

            error = "Windows currently supports only INT, TERM, and KILL semantics through forced process termination.";
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

    private static class Interop
    {
        [DllImport("libc", SetLastError = true)]
        public static extern int kill(int pid, int sig);
    }
}
