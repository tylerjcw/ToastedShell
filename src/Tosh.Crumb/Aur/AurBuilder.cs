using System.Diagnostics;
using Tosh.Crumb.Output;
using Tosh.Crumb.Pacman;

namespace Tosh.Crumb.Aur;

/// <summary>
/// Drives the AUR build cycle for a package: clone (or fetch) into a
/// per-user cache, optionally let the user review the PKGBUILD, then run
/// <c>makepkg -si</c>. The makepkg invocation handles its own privilege
/// escalation (it refuses to run as root and shells out to sudo/doas to
/// install).
/// </summary>
public static class AurBuilder
{
    /// <summary>Returns the cache root used for AUR clones.</summary>
    public static string CacheDir
    {
        get
        {
            var xdg = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
            if (string.IsNullOrEmpty(xdg))
            {
                var home = Environment.GetEnvironmentVariable("HOME")
                    ?? throw new InvalidOperationException("$HOME is not set");
                xdg = Path.Combine(home, ".cache");
            }
            return Path.Combine(xdg, "crumb", "aur");
        }
    }

    /// <summary>Log directory used when build output is suppressed.</summary>
    public static string LogDir
    {
        get
        {
            var xdg = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
            if (string.IsNullOrEmpty(xdg))
            {
                var home = Environment.GetEnvironmentVariable("HOME")
                    ?? throw new InvalidOperationException("$HOME is not set");
                xdg = Path.Combine(home, ".cache");
            }
            return Path.Combine(xdg, "crumb", "log");
        }
    }

    public sealed record BuildOptions(
        bool NoConfirm = false,
        bool AsDeps = false,
        bool Clean = false,
        bool Quiet = true,
        bool SkipGpgImport = false,
        string? Pager = null);

    /// <summary>
    /// Clone the AUR repo for <paramref name="pkg"/> into the per-user
    /// cache, or refresh an existing checkout. Returns the working
    /// directory on success, or <c>null</c> if the package has no
    /// PKGBUILD (typo or removed package).
    /// </summary>
    public static async Task<string?> EnsureClonedAsync(string pkg, CancellationToken ct)
    {
        Directory.CreateDirectory(CacheDir);
        Directory.CreateDirectory(LogDir);
        var dir = Path.Combine(CacheDir, pkg);
        var fresh = !Directory.Exists(dir);
        // Route git's chatter ("remote: Total 0...", "HEAD is now at ...")
        // into a log file rather than the user's terminal — paru does the
        // same. The log path is per-package so a later build failure can
        // still surface relevant fetch errors.
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        var clonelog = Path.Combine(LogDir, $"{pkg}-clone-{stamp}.log");
        if (fresh)
        {
            var clone = await RunAsync("git", new[]
            {
                "clone", "--depth", "1", "--quiet",
                $"https://aur.archlinux.org/{pkg}.git",
                dir,
            }, workdir: CacheDir, clonelog, ct);
            if (clone != 0) return null;
        }
        else
        {
            var fetch = await RunAsync("git", new[] { "fetch", "--depth", "1", "--quiet", "origin" }, dir, clonelog, ct);
            if (fetch == 0)
                await RunAsync("git", new[] { "reset", "--hard", "--quiet", "origin/HEAD" }, dir, clonelog, ct);
        }
        // Successful clones don't need their log kept around.
        try { if (File.Exists(clonelog) && new FileInfo(clonelog).Length == 0) File.Delete(clonelog); } catch { }
        return File.Exists(Path.Combine(dir, "PKGBUILD")) ? dir : null;
    }

    /// <summary>
    /// Paru-style single up-front review: prompt once for the whole
    /// batch, page each PKGBUILD (and install hook) in sequence,
    /// record the reviewed HEAD per package, then return a final
    /// proceed/abort decision.
    /// </summary>
    /// <returns>true to proceed with the build, false to abort.</returns>
    public static async Task<bool> BatchReviewAsync(
        IReadOnlyList<(string Pkg, string Dir)> targets,
        string? pagerOverride,
        bool diffMode,
        CancellationToken ct)
    {
        if (targets.Count == 0) return true;

        var pager = pagerOverride
            ?? Environment.GetEnvironmentVariable("CRUMB_PAGER")
            ?? Environment.GetEnvironmentVariable("PAGER")
            ?? "less";

        var label = targets.Count == 1 ? "PKGBUILD" : $"{targets.Count} PKGBUILDs";
        if (Output.Confirm.YesNo($":: Review {label}?", defaultYes: false))
        {
            var cache = ReviewCache.Load();
            foreach (var (pkg, dir) in targets)
            {
                var pkgbuild = Path.Combine(dir, "PKGBUILD");
                var lastSha = cache.LastReviewed(pkg);
                var head = await ReviewCache.HeadShaAsync(dir, ct);
                var showDiff = diffMode && lastSha is not null && head is not null
                    && !string.Equals(lastSha, head, StringComparison.OrdinalIgnoreCase);
                if (lastSha is not null && head is not null
                    && string.Equals(lastSha, head, StringComparison.OrdinalIgnoreCase))
                {
                    Output.Confirm.Status($"crumb: PKGBUILD for '{pkg}' unchanged since last review ({Short(head)})");
                }
                else if (showDiff)
                {
                    Output.Confirm.Status($"crumb: '{pkg}' changes {Short(lastSha!)}..{Short(head!)}");
                    await RunAsync("git", new[] { "--no-pager", "log", "--oneline", $"{lastSha}..HEAD" }, dir, logPath: null, ct);
                    await RunAsync("git", new[] { "-c", $"core.pager={pager}", "diff", $"{lastSha}..HEAD" }, dir, logPath: null, ct);
                }
                else
                {
                    Output.Confirm.Status($"crumb: PKGBUILD for '{pkg}': {pkgbuild}");
                    await RunAsync(pager, new[] { pkgbuild }, dir, logPath: null, ct);
                    foreach (var aux in Directory.EnumerateFiles(dir, "*.install"))
                    {
                        Output.Confirm.Status($"crumb: install hook: {aux}");
                        await RunAsync(pager, new[] { aux }, dir, logPath: null, ct);
                    }
                }
                if (head is not null) cache.Record(pkg, head);
            }
            cache.Save();
            return Output.Confirm.YesNo($":: Proceed with build of {targets.Count} package(s)?", defaultYes: true);
        }
        return true;
    }

    /// <summary>Build and install a single AUR package.</summary>
    public static async Task<int> BuildAndInstallAsync(
        string pkg,
        BuildOptions options,
        DevelTracker? devel = null,
        CancellationToken ct = default)
    {
        var dir = await EnsureClonedAsync(pkg, ct);
        if (dir is null)
        {
            Console.Error.WriteLine($"crumb: no PKGBUILD in clone of '{pkg}' — is the package name correct?");
            return 1;
        }

        string? logPath = null;
        if (options.Quiet)
        {
            Directory.CreateDirectory(LogDir);
            var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            logPath = Path.Combine(LogDir, $"{pkg}-{stamp}.log");
        }

        // PGP keys for source signatures: prompt + import any not
        // already in the user's keyring before makepkg discovers
        // the integrity-check failure the hard way.
        if (!options.SkipGpgImport)
        {
            var srcinfo = Path.Combine(dir, ".SRCINFO");
            if (!await GpgKeys.EnsureImportedAsync(srcinfo, options.NoConfirm, ct))
            {
                Console.Error.WriteLine($"crumb: missing PGP keys for '{pkg}', aborting build");
                return 1;
            }
        }

        // When NoConfirm is set we cannot just pass `-i` to makepkg: it
        // forwards build flags but not `--noconfirm` to its embedded
        // `pacman -U`, so pacman re-prompts on inherited stdin (which
        // through .NET's redirection ends up on a broken pseudo-TTY).
        // Workaround: build with makepkg, then install the produced
        // packages ourselves via the standard escalation path with an
        // explicit `--noconfirm`.
        if (options.NoConfirm)
        {
            Confirm.Status($":: Building {pkg}...");
            var buildArgs = new List<string> { "--noconfirm", "-s", "-f" };
            if (options.AsDeps) buildArgs.Add("--asdeps");
            if (options.Clean) buildArgs.Add("--clean");
            var brc = await RunAsync("makepkg", buildArgs, workdir: dir, logPath, ct);
            if (brc != 0) return Fail(pkg, "build", brc, logPath);

            var built = await ListBuiltPackagesAsync(dir, ct);
            if (built.Count == 0)
            {
                Console.Error.WriteLine($"crumb: makepkg produced no packages for '{pkg}'");
                return 1;
            }

            Confirm.Status($":: Installing {pkg}...");
            var pacArgs = new List<string> { "pacman", "-U", "--noconfirm" };
            if (options.AsDeps) pacArgs.Add("--asdeps");
            pacArgs.AddRange(built);
            var irc = await RunEscalatedAsync(pacArgs, logPath, ct);
            if (irc != 0) return Fail(pkg, "install", irc, logPath);
            await RecordDevelAsync(pkg, dir, devel, ct);
            Confirm.Status($"  ✓ {pkg}");
            return 0;
        }

        // Interactive (verbose-equivalent) path: makepkg drives stdio
        // directly and handles its own install prompt.
        var args = new List<string> { "-si" };
        if (options.AsDeps) args.Add("--asdeps");
        if (options.Clean) args.Add("--clean");
        var ircI = await RunAsync("makepkg", args, workdir: dir, logPath: null, ct);
        if (ircI == 0) await RecordDevelAsync(pkg, dir, devel, ct);
        return ircI;
    }

    private static async Task RecordDevelAsync(string pkg, string dir, DevelTracker? devel, CancellationToken ct)
    {
        if (devel is null) return;
        var srcinfo = Path.Combine(dir, ".SRCINFO");
        try { await devel.RecordAsync(pkg, srcinfo, ct); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"crumb: warning: devel record failed for '{pkg}': {ex.Message}");
        }
    }

    private static int Fail(string pkg, string phase, int code, string? logPath)
    {
        Console.Error.WriteLine($"crumb: {phase} failed for '{pkg}' (exit {code})");
        if (logPath is not null && File.Exists(logPath))
        {
            Console.Error.WriteLine($"crumb: log: {logPath}");
            try
            {
                var tail = File.ReadLines(logPath).TakeLast(20).ToList();
                foreach (var l in tail) Console.Error.WriteLine("  " + l);
            }
            catch { /* best effort */ }
        }
        return code;
    }

    /// <summary>
    /// Asks makepkg where the just-built package files live (one path
    /// per line, typically one for the main package plus any split or
    /// debug packages).
    /// </summary>
    private static async Task<List<string>> ListBuiltPackagesAsync(string dir, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "makepkg",
            WorkingDirectory = dir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("--packagelist");
        var paths = new List<string>();
        try
        {
            using var p = Process.Start(psi);
            if (p is null) return paths;
            string? line;
            while ((line = await p.StandardOutput.ReadLineAsync(ct)) is not null)
            {
                line = line.Trim();
                if (line.Length > 0 && File.Exists(line)) paths.Add(line);
            }
            await p.WaitForExitAsync(ct);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"crumb: makepkg --packagelist failed: {ex.Message}");
        }
        return paths;
    }

    private static async Task<int> RunEscalatedAsync(List<string> commandWithArgs, string? logPath, CancellationToken ct)
    {
        var wrapped = Privilege.Wrap(commandWithArgs);
        if (wrapped.Count == 0) return 1;
        var psi = new ProcessStartInfo(wrapped[0]) { UseShellExecute = false };
        for (var i = 1; i < wrapped.Count; i++) psi.ArgumentList.Add(wrapped[i]);
        if (logPath is not null)
        {
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
        }
        try
        {
            using var ttyScope = logPath is null ? Tosh.Client.ChildTtyScope.Acquire() : null;
            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException($"failed to start {wrapped[0]}");
            await PumpAsync(proc, logPath, ct);
            await proc.WaitForExitAsync(ct);
            return proc.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Console.Error.WriteLine($"crumb: cannot exec '{wrapped[0]}': {ex.Message}");
            return 127;
        }
    }

    private static string Short(string sha) => sha.Length >= 7 ? sha[..7] : sha;

    private static async Task<int> RunAsync(string file, IEnumerable<string> args, string workdir, string? logPath, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = file,
            WorkingDirectory = workdir,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (logPath is not null)
        {
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
        }

        try
        {
            using var ttyScope = logPath is null ? Tosh.Client.ChildTtyScope.Acquire() : null;
            using var p = Process.Start(psi)
                ?? throw new InvalidOperationException($"failed to start {file}");
            await PumpAsync(p, logPath, ct);
            await p.WaitForExitAsync(ct);
            return p.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Console.Error.WriteLine($"crumb: cannot run '{file}': {ex.Message}");
            return 127;
        }
    }

    /// <summary>
    /// When <paramref name="logPath"/> is provided, drains both stdout
    /// and stderr of <paramref name="proc"/> into that file
    /// (appending). When it is null this is a no-op and the child
    /// inherits the parent's streams directly.
    /// </summary>
    private static async Task PumpAsync(Process proc, string? logPath, CancellationToken ct)
    {
        if (logPath is null) return;
        await using var log = new FileStream(
            logPath, FileMode.Append, FileAccess.Write, FileShare.Read,
            bufferSize: 4096, useAsync: true);
        using var gate = new SemaphoreSlim(1, 1);
        var t1 = CopyAsync(proc.StandardOutput.BaseStream, log, gate, ct);
        var t2 = CopyAsync(proc.StandardError.BaseStream, log, gate, ct);
        await Task.WhenAll(t1, t2);
    }

    private static async Task CopyAsync(Stream src, Stream dst, SemaphoreSlim gate, CancellationToken ct)
    {
        var buf = new byte[4096];
        int n;
        while ((n = await src.ReadAsync(buf.AsMemory(), ct)) > 0)
        {
            await gate.WaitAsync(ct);
            try
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                await dst.FlushAsync(ct);
            }
            finally
            {
                gate.Release();
            }
        }
    }
}
