using System.Diagnostics;

namespace Tosh.Crumb.Aur;

/// <summary>
/// Thin wrapper around pacman's <c>vercmp</c> binary so we never
/// hand-roll alpm's version comparison rules. Returns the sign of the
/// comparison: -1 / 0 / 1, mirroring <c>vercmp</c>'s stdout.
/// </summary>
internal static class Vercmp
{
    /// <summary>
    /// Compares two version strings using <c>vercmp</c>. Returns
    /// <c>null</c> if the binary is unavailable or fails — callers
    /// should treat that as "cannot decide" and fall back to a string
    /// inequality check.
    /// </summary>
    public static int? Compare(string a, string b)
    {
        try
        {
            var psi = new ProcessStartInfo("vercmp")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add(a);
            psi.ArgumentList.Add(b);
            using var p = Process.Start(psi);
            if (p is null) return null;

            // Both pipes are started before waiting. Reading one to the end while the
            // other fills its buffer is the classic way to deadlock on a redirected
            // child; `vercmp` never writes enough to hit it, but the shape is wrong and
            // the next thing bound this way might.
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();

            // A version comparison is a millisecond of work. If it has not finished in
            // five seconds something is wrong with the process rather than the input,
            // and an upgrade check should not hang on it.
            if (!p.WaitForExit(milliseconds: 5_000))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
                return null;
            }

            _ = stderr;
            return int.TryParse(stdout.GetAwaiter().GetResult().Trim(), out var n) ? Math.Sign(n) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>True when <paramref name="installed"/> is strictly older than <paramref name="candidate"/>.</summary>
    public static bool IsOlder(string installed, string candidate)
    {
        // Identical strings are the common case during `-Syu` — most installed packages
        // are already current — and comparing a version with itself cannot need a
        // process. On a machine with 139 foreign packages this is most of them.
        if (string.Equals(installed, candidate, StringComparison.Ordinal)) return false;

        var r = Compare(installed, candidate);
        if (r is not null) return r.Value < 0;
        // No vercmp available — fall back to ordinal inequality. Better
        // to over-rebuild than to silently skip a real upgrade.
        return !string.Equals(installed, candidate, StringComparison.Ordinal);
    }
}
