using Tosh.Crumb.Aur;
using Tosh.Crumb.Output;
using Tosh.Crumb.Pacman;

namespace Tosh.Crumb.Commands;

public static partial class CrumbCommands
{
    public static async Task<int> RemoveAsync(CrumbOptions opt, CancellationToken ct)
    {
        if (opt.Positional.Count == 0)
        {
            Console.Error.WriteLine("crumb remove: missing package name");
            return 2;
        }
        var flag = "-R";
        if (opt.Recursive) flag += "s";
        if (opt.Cascade) flag += "c";
        if (opt.NoSave) flag += "n";

        // Render the removal plan from the local DB so the user sees
        // the boxed list (with installed versions and originating repo)
        // before pacman runs. Missing packages still get passed to
        // pacman, which will surface its own error.
        var db = new PacmanDb();
        var rows = new List<(string Source, string Name, string Version)>();
        foreach (var name in opt.Positional)
        {
            if (db.Local.TryGetValue(name, out var p))
            {
                rows.Add((string.IsNullOrEmpty(p.Repo) ? "local" : p.Repo, p.Name, p.Version));
            }
            else
            {
                rows.Add(("?", name, string.Empty));
            }
        }
        if (rows.Count > 0)
            UpgradeListFormatter.RenderPlan(rows, "Packages to remove");

        var args = new List<string> { "pacman", flag };
        if (opt.NoConfirm) args.Add("--noconfirm");
        args.AddRange(opt.Positional);

        var rc = await RunEscalatedAsync(args, opt.DryRun, ct);

        var summary = new List<(string Phase, UpgradeListFormatter.ResultStatus Status, string Detail)>();
        if (!opt.DryRun)
        {
            summary.Add(("Remove",
                rc == 0 ? UpgradeListFormatter.ResultStatus.Success : UpgradeListFormatter.ResultStatus.Failed,
                rc == 0 ? $"{opt.Positional.Count} package(s) removed" : $"pacman exit {rc}"));
            UpgradeListFormatter.RenderSummary(summary);
        }

        // Tidy the devel cache: if pacman succeeded, any tracked
        // entries for the removed packages are now stale. Soft-fail
        // on tracker errors — pacman has already done the work.
        if (rc == 0 && !opt.DryRun)
        {
            try
            {
                var tracker = DevelTracker.Load();
                var changed = false;
                foreach (var pkg in opt.Positional)
                    if (tracker.Forget(pkg)) changed = true;
                if (changed) tracker.Save();
            }
            catch { /* best effort */ }
        }

        return rc;
    }

    public static Task<int> CleanAsync(CrumbOptions opt, CancellationToken ct)
    {
        var dir = AurBuilder.CacheDir;
        if (!Directory.Exists(dir))
        {
            Console.WriteLine($"crumb: AUR cache already empty ({dir})");
            return Task.FromResult(0);
        }

        var entries = Directory.EnumerateFileSystemEntries(dir).ToList();
        if (entries.Count == 0)
        {
            Console.WriteLine($"crumb: AUR cache already empty ({dir})");
            return Task.FromResult(0);
        }

        if (opt.DryRun)
        {
            Console.WriteLine($"crumb: dry-run: would remove {entries.Count} entr{(entries.Count == 1 ? "y" : "ies")} from {dir}");
            foreach (var entry in entries) Console.WriteLine($"  {Path.GetFileName(entry)}");
            return Task.FromResult(0);
        }

        var removed = 0;
        foreach (var entry in entries)
        {
            try
            {
                if (Directory.Exists(entry)) Directory.Delete(entry, recursive: true);
                else File.Delete(entry);
                removed++;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"crumb: failed to remove '{entry}': {ex.Message}");
            }
        }
        Console.WriteLine($"crumb: removed {removed} entr{(removed == 1 ? "y" : "ies")} from {dir}");
        return Task.FromResult(removed == entries.Count ? 0 : 1);
    }

    public static async Task<int> SyncAsync(CrumbOptions opt, CancellationToken ct)
    {
        // `crumb sync` ⇒ `pacman -Sy`; the cluster expander already routes
        // `-Sy` here, so the --refresh flag is implicit. A future --force
        // option may map to `-Syy`.
        var args = new List<string> { "pacman", "-Sy" };
        if (opt.NoConfirm) args.Add("--noconfirm");
        return await RunEscalatedAsync(args, opt.DryRun, ct);
    }
}
