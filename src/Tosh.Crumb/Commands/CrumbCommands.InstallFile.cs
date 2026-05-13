using Tosh.Crumb.Output;

namespace Tosh.Crumb.Commands;

public static partial class CrumbCommands
{
    public static async Task<int> InstallFileAsync(CrumbOptions opt, CancellationToken ct)
    {
        if (opt.Positional.Count == 0)
        {
            Console.Error.WriteLine("crumb -U: missing package file");
            return 2;
        }

        var rows = opt.Positional
            .Select(p => ("file", p, File.Exists(p) ? FormatFileSize(new FileInfo(p).Length) : string.Empty))
            .ToList();
        UpgradeListFormatter.RenderPlan(rows, "Package files to install", opt.GroupBy);

        if (!opt.NoConfirm && !opt.DryRun)
        {
            if (!Confirm.YesNo("Install package file(s)?"))
            {
                Console.Error.WriteLine("crumb: cancelled");
                UpgradeListFormatter.RenderSummary(new[]
                {
                    ("File install", UpgradeListFormatter.ResultStatus.Skipped, "cancelled by user"),
                });
                return 1;
            }
        }

        var args = new List<string> { "pacman", "-U", "--noconfirm" };
        if (opt.Needed) args.Add("--needed");
        if (opt.AsDeps) args.Add("--asdeps");
        args.AddRange(opt.Positional);

        var rc = await RunEscalatedAsync(args, opt.DryRun, ct);
        if (!opt.DryRun)
        {
            UpgradeListFormatter.RenderSummary(new[]
            {
                ("File install",
                    rc == 0 ? UpgradeListFormatter.ResultStatus.Success : UpgradeListFormatter.ResultStatus.Failed,
                    rc == 0 ? $"{opt.Positional.Count} file(s) installed" : $"pacman exit {rc}"),
            });
        }
        return rc;
    }

    private static string FormatFileSize(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit + 1 < units.Length)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} B" : $"{value:0.0} {units[unit]}";
    }
}
