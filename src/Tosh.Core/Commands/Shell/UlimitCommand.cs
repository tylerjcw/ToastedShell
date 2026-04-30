using System.Globalization;
using System.Runtime.InteropServices;

namespace Tosh.Core.Commands.Shell;

[Stdlib(StdlibCategory.Shell)]
[CommandCategory("Shell")]
[CommandArgument("resource", "The resource limit to query or set (e.g. 'nofile', 'nproc', 'stack', 'core', 'fsize').", Required = false)]
[CommandArgument("value", "The new soft limit value to set, or 'unlimited'.", Required = false)]
[CommandOption("-H", "Show or set the hard limit instead of the soft limit.")]
[CommandOption("-a", "Show all resource limits.")]
[CommandExample("ulimit", Title = "Show all resource limits")]
[CommandExample("ulimit -a", Title = "Show all resource limits")]
[CommandExample("ulimit nofile", Title = "Show the soft file descriptor limit")]
[CommandExample("ulimit nofile 4096", Title = "Set the soft file descriptor limit to 4096")]
[CommandExample("ulimit -H nofile", Title = "Show the hard file descriptor limit")]
[CommandNote("Displays or sets POSIX resource limits (via getrlimit/setrlimit). Only available on Unix-like systems. Setting a hard limit requires root privileges. Use 'unlimited' to remove a soft limit (set to hard limit).")]
[CommandOutput("A table of resource limits when no arguments given, or a single value when querying a specific resource.")]
public sealed class UlimitCommand : ShellCommand
{
    public UlimitCommand()
        : base("ulimit", "Display or set resource limits.", "ulimit [-H] [-a] [resource [value]]") { }

    private static readonly (string Name, string Description, int Resource, long Divisor)[] Resources =
    [
        ("core",     "core file size (blocks)",     4,  512),  // RLIMIT_CORE
        ("data",     "data seg size (kbytes)",       2,  1024), // RLIMIT_DATA
        ("fsize",    "file size (blocks)",           1,  512),  // RLIMIT_FSIZE
        ("memlock",  "max locked memory (kbytes)",   8,  1024), // RLIMIT_MEMLOCK
        ("nofile",   "open files",                   7,  1),    // RLIMIT_NOFILE
        ("nproc",    "max user processes",           6,  1),    // RLIMIT_NPROC
        ("rss",      "max resident set (kbytes)",    5,  1024), // RLIMIT_RSS
        ("stack",    "stack size (kbytes)",           3,  1024), // RLIMIT_STACK
        ("cpu",      "cpu time (seconds)",           0,  1),    // RLIMIT_CPU
        ("as",       "virtual memory (kbytes)",      9,  1024), // RLIMIT_AS
        ("msgqueue", "POSIX message queues (bytes)", 12, 1),    // RLIMIT_MSGQUEUE
        ("nice",     "max nice priority",            13, 1),    // RLIMIT_NICE
        ("rtprio",   "max realtime priority",        14, 1),    // RLIMIT_RTPRIO
        ("sigpending","pending signals",             11, 1),    // RLIMIT_SIGPENDING
        ("locks",    "file locks",                   10, 1),    // RLIMIT_LOCKS
    ];

    // Sentinel for unlimited.
    private const ulong RlimInfinity = unchecked((ulong)-1);

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (OperatingSystem.IsWindows())
        {
            throw context.CreateDiagnostic(
                code: "tosh.runtime.ulimit_unsupported",
                title: "`ulimit` is not supported on Windows.",
                label: "this command requires a Unix-like operating system");
        }

        var hard = false;
        var showAll = false;
        string? resourceName = null;
        string? newValue = null;

        // Parse arguments.
        var positionalIndex = 0;
        foreach (var arg in context.Arguments)
        {
            var s = arg?.ToString() ?? string.Empty;
            if (s == "-H") { hard = true; continue; }
            if (s == "-a") { showAll = true; continue; }

            if (positionalIndex == 0) { resourceName = s; positionalIndex++; }
            else if (positionalIndex == 1) { newValue = s; positionalIndex++; }
        }

        // Show all limits.
        if (resourceName is null || showAll)
        {
            foreach (var entry in Resources)
            {
                if (NativeRlimit.getrlimit(entry.Resource, out var rlim) == 0)
                {
                    var val = hard ? rlim.HardLimit : rlim.SoftLimit;
                    var display = FormatLimit(val, entry.Divisor);
                    yield return ShellRecordUtilities.CreateExpando(
                    [
                        new("resource", entry.Name),
                        new("description", entry.Description),
                        new(hard ? "hard" : "soft", display),
                    ]);
                }
            }

            yield break;
        }

        // Find the resource.
        var match = Array.Find(Resources, r => r.Name.Equals(resourceName, StringComparison.OrdinalIgnoreCase));
        if (match.Name is null)
        {
            throw context.CreateDiagnostic(
                code: "tosh.runtime.ulimit_unknown_resource",
                title: $"Unknown resource '{resourceName}'.",
                label: $"expected one of: {string.Join(", ", Resources.Select(r => r.Name))}");
        }

        // Set a new limit.
        if (newValue is not null)
        {
            if (NativeRlimit.getrlimit(match.Resource, out var current) != 0)
            {
                throw context.CreateDiagnostic(
                    code: "tosh.runtime.ulimit_getrlimit_failed",
                    title: $"Failed to read current limit for '{resourceName}'.",
                    label: "getrlimit failed");
            }

            ulong parsed;
            if (newValue.Equals("unlimited", StringComparison.OrdinalIgnoreCase))
            {
                parsed = RlimInfinity;
            }
            else if (ulong.TryParse(newValue, NumberStyles.None, CultureInfo.InvariantCulture, out var raw))
            {
                parsed = raw * (ulong)match.Divisor;
            }
            else
            {
                throw context.CreateDiagnostic(
                    code: "tosh.runtime.ulimit_invalid_value",
                    title: $"Invalid limit value '{newValue}'.",
                    label: "expected a non-negative integer or 'unlimited'");
            }

            if (hard)
            {
                current.HardLimit = parsed;
            }
            else
            {
                current.SoftLimit = parsed;
            }

            if (NativeRlimit.setrlimit(match.Resource, ref current) != 0)
            {
                var errno = Marshal.GetLastWin32Error();
                throw context.CreateDiagnostic(
                    code: "tosh.runtime.ulimit_setrlimit_failed",
                    title: $"Failed to set limit for '{resourceName}' (errno {errno}).",
                    label: hard ? "setting hard limit may require root privileges" : "value may exceed the hard limit");
            }

            yield break;
        }

        // Query a specific resource.
        if (NativeRlimit.getrlimit(match.Resource, out var rl) != 0)
        {
            throw context.CreateDiagnostic(
                code: "tosh.runtime.ulimit_getrlimit_failed",
                title: $"Failed to read limit for '{resourceName}'.",
                label: "getrlimit failed");
        }

        var value2 = hard ? rl.HardLimit : rl.SoftLimit;
        yield return FormatLimit(value2, match.Divisor);
    }

    private static string FormatLimit(ulong value, long divisor)
    {
        if (value == RlimInfinity)
        {
            return "unlimited";
        }

        return (value / (ulong)divisor).ToString(CultureInfo.InvariantCulture);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rlimit
    {
        public ulong SoftLimit; // rlim_cur
        public ulong HardLimit; // rlim_max
    }

    private static class NativeRlimit
    {
        [DllImport("libc", SetLastError = true)]
        public static extern int getrlimit(int resource, out Rlimit rlim);

        [DllImport("libc", SetLastError = true)]
        public static extern int setrlimit(int resource, ref Rlimit rlim);
    }
}
