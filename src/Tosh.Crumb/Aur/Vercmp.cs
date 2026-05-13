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
            var stdout = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit();
            return int.TryParse(stdout, out var n) ? Math.Sign(n) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>True when <paramref name="installed"/> is strictly older than <paramref name="candidate"/>.</summary>
    public static bool IsOlder(string installed, string candidate)
    {
        var r = Compare(installed, candidate);
        if (r is not null) return r.Value < 0;
        // No vercmp available — fall back to ordinal inequality. Better
        // to over-rebuild than to silently skip a real upgrade.
        return !string.Equals(installed, candidate, StringComparison.Ordinal);
    }
}
