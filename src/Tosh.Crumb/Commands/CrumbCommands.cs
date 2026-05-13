using Tosh.Crumb.Models;
using Tosh.Crumb.Pacman;

namespace Tosh.Crumb.Commands;

/// <summary>
/// Subcommand dispatch surface. The class is partial — each
/// subcommand lives in its own file
/// (<c>CrumbCommands.Install.cs</c>, etc.); this file holds the
/// helpers they share.
/// </summary>
public static partial class CrumbCommands
{
    /// <summary>
    /// Packages owned by the tosh suite — these share names with
    /// unrelated AUR projects (notably <c>crumb</c>), so we exclude
    /// them from foreign-package AUR rebuild scans to avoid clobbering
    /// the locally-built versions with random upstream code.
    /// </summary>
    private static readonly HashSet<string> ToshSuitePackages = new(StringComparer.Ordinal)
    {
        "tosh", "tosh-lsp", "tosh-mcp", "tome", "crumb",
    };

    private static string? FindLocalDir(string root, string name)
    {
        if (!Directory.Exists(root)) return null;
        // Local dirs are "<name>-<version>"; we want the one matching <name>.
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            var basename = Path.GetFileName(dir);
            var dash = basename.LastIndexOf('-');
            if (dash <= 0) continue;
            var prevDash = basename.LastIndexOf('-', dash - 1);
            if (prevDash <= 0) continue;
            var pkgName = basename[..prevDash];
            if (string.Equals(pkgName, name, StringComparison.Ordinal)) return dir;
        }
        return null;
    }

    private static Package AnnotateInstalled(Package p, IReadOnlyDictionary<string, Package> local)
    {
        if (!local.TryGetValue(p.Name, out var installed)) return p;
        return p with
        {
            Installed = true,
            InstalledVersion = installed.Version,
            InstallReason = installed.InstallReason,
        };
    }

    private static string StripVersionConstraint(string dep)
    {
        // "foo>=1.2" → "foo"
        var i = dep.AsSpan().IndexOfAny('>', '<', '=');
        return i < 0 ? dep : dep[..i];
    }

    private static async Task<int> RunEscalatedAsync(List<string> commandWithArgs, bool dryRun, CancellationToken ct)
    {
        var wrapped = Privilege.Wrap(commandWithArgs);
        if (dryRun)
        {
            Console.WriteLine("crumb: dry-run: " + string.Join(' ', wrapped));
            return 0;
        }
        if (wrapped.Count == 0)
        {
            Console.Error.WriteLine("crumb: no command resolved");
            return 1;
        }
        var psi = new System.Diagnostics.ProcessStartInfo(wrapped[0]) { UseShellExecute = false };
        for (var i = 1; i < wrapped.Count; i++) psi.ArgumentList.Add(wrapped[i]);
        try
        {
            using var ttyScope = Tosh.Client.ChildTtyScope.Acquire();
            using var proc = System.Diagnostics.Process.Start(psi)
                ?? throw new InvalidOperationException($"failed to start {wrapped[0]}");
            await proc.WaitForExitAsync(ct);
            return proc.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Console.Error.WriteLine($"crumb: cannot exec '{wrapped[0]}': {ex.Message}");
            return 127;
        }
    }

    /// <summary>
    /// Runs <c>pacman -Qu</c> (read-only, no escalation) and returns
    /// one line per pending upgrade: <c>name oldver -&gt; newver</c>.
    /// </summary>
    private static async Task<List<string>> ListPendingUpgradesAsync(CancellationToken ct)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("pacman")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-Qu");
        var lines = new List<string>();
        try
        {
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc is null) return lines;
            string? line;
            while ((line = await proc.StandardOutput.ReadLineAsync(ct)) is not null)
            {
                if (line.Length > 0) lines.Add(line);
            }
            await proc.WaitForExitAsync(ct);
            // pacman -Qu returns 1 when there's nothing to upgrade; that's not an error.
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"crumb: pacman -Qu failed: {ex.Message}");
        }
        return lines;
    }
}
