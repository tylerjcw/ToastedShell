namespace Tosh.Crumb.Pacman;

/// <summary>
/// Resolves the privilege-escalation tool used to invoke pacman.
///
/// Precedence:
///   1. <c>CRUMB_SUDO</c> environment variable (verbatim — may be a command
///      with embedded args, e.g. <c>"sudo -E"</c>).
///   2. First of <c>doas</c>, <c>sudo</c>, <c>pkexec</c> found on PATH.
///   3. Empty (already root, or no escalation available).
/// </summary>
public static class Privilege
{
    public static bool IsRoot =>
        Environment.GetEnvironmentVariable("USER") == "root" ||
        (System.Runtime.InteropServices.RuntimeInformation
            .IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Linux)
         && getuid() == 0);

    /// <summary>Resolved escalator tokens (file + leading args), or empty when running as root.</summary>
    public static IReadOnlyList<string> ResolveEscalator()
    {
        if (IsRoot) return Array.Empty<string>();

        var explicitTool = Environment.GetEnvironmentVariable("CRUMB_SUDO");
        if (!string.IsNullOrWhiteSpace(explicitTool))
        {
            return SplitCommand(explicitTool);
        }

        foreach (var name in new[] { "doas", "sudo", "pkexec" })
        {
            if (LookupOnPath(name) is { } path)
                return new[] { path };
        }
        return Array.Empty<string>();
    }

    /// <summary>Prepends the escalator (if any) to <paramref name="commandWithArgs"/>.</summary>
    public static List<string> Wrap(IEnumerable<string> commandWithArgs)
    {
        var result = new List<string>(ResolveEscalator());
        result.AddRange(commandWithArgs);
        return result;
    }

    private static List<string> SplitCommand(string s)
    {
        // Minimal whitespace split — we don't try to be a full shell parser.
        // Users who need quoted arguments can set CRUMB_SUDO to just the
        // tool and rely on its own defaults.
        var parts = s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.ToList();
    }

    private static string? LookupOnPath(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir, name);
                if (File.Exists(candidate)) return candidate;
            }
            catch { /* unreadable dir — skip */ }
        }
        return null;
    }

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "getuid")]
    private static extern uint getuid();
}
