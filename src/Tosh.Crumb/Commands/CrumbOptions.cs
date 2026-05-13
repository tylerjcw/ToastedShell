using Tosh.Crumb.Output;

namespace Tosh.Crumb.Commands;

/// <summary>
/// Parsed command-line options shared across subcommands.
/// </summary>
public sealed class CrumbOptions
{
    public string? GroupBy { get; set; }
    public OutputFormat Format { get; set; } = OutputFormat.Auto;
    public bool Verbose { get; set; }
    public bool Quiet { get; set; }       // suppress live build output, log to file instead
    public bool ReposOnly { get; set; }
    public bool AurOnly { get; set; }
    public bool InstalledOnly { get; set; }
    public bool ExplicitOnly { get; set; }
    public bool ForeignOnly { get; set; }
    public bool OrphansOnly { get; set; }
    public string SearchBy { get; set; } = "name-desc";

    // Mutation flags (install / remove / update).
    public bool Refresh { get; set; }       // -y
    public bool Upgrade { get; set; }       // -u
    public bool NoConfirm { get; set; }
    public bool Needed { get; set; }
    public bool AsDeps { get; set; }
    public bool Review { get; set; }
    public bool NoReview { get; set; }
    public bool Recursive { get; set; }     // -Rs
    public bool Cascade { get; set; }       // -Rc
    public bool NoSave { get; set; }        // -Rn
    public bool DryRun { get; set; }
    public bool SudoLoop { get; set; }      // keep sudo ticket fresh during AUR builds
    public bool DiffReview { get; set; }    // show only diff vs last reviewed commit

    // `crumb news` knobs.
    public int? Limit { get; set; }
    public bool NewsAll { get; set; }
    public DateTimeOffset? NewsSince { get; set; }
    public string? NewsFeed { get; set; }

    public List<string> Positional { get; } = new();
    public static CrumbOptions Parse(IReadOnlyList<string> args)
    {
        var opt = new CrumbOptions();
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            switch (a)
            {
                case "--format":
                case "-f":
                    if (++i >= args.Count) throw new ArgumentException("--format requires a value");
                    opt.Format = ParseFormat(args[i]);
                    break;
                case var s when s.StartsWith("--format=", StringComparison.Ordinal):
                    opt.Format = ParseFormat(a["--format=".Length..]);
                    break;
                case "--group-by":
                    if (++i >= args.Count) throw new ArgumentException("--group-by requires a value");
                    opt.GroupBy = args[i];
                    break;
                case var s when s.StartsWith("--group-by=", StringComparison.Ordinal):
                    opt.GroupBy = a["--group-by=".Length..];
                    break;
                case "--json": opt.Format = OutputFormat.Json; break;
                case "--ndjson": opt.Format = OutputFormat.Ndjson; break;
                case "--tsv": opt.Format = OutputFormat.Tsv; break;
                case "--names": opt.Format = OutputFormat.Names; break;
                case "-J": opt.Format = OutputFormat.Json; break;
                case "-N": opt.Format = OutputFormat.Ndjson; break;
                case "-T": opt.Format = OutputFormat.Tsv; break;
                case "-q": opt.Format = OutputFormat.Names; break;
                case "-v":
                case "--verbose": opt.Verbose = true; break;
                case "--quiet": opt.Quiet = true; break;
                case "--repo":
                case "--repos":
                case "--repos-only": opt.ReposOnly = true; break;
                case "--aur":
                case "--aur-only": opt.AurOnly = true; break;
                case "--installed":
                case "-i": opt.InstalledOnly = true; break;
                case "--explicit": opt.ExplicitOnly = true; break;
                case "--foreign": opt.ForeignOnly = true; break;
                case "--orphans": opt.OrphansOnly = true; break;
                case "--by":
                    if (++i >= args.Count) throw new ArgumentException("--by requires a value");
                    opt.SearchBy = args[i]; break;
                case var s when s.StartsWith("--by=", StringComparison.Ordinal):
                    opt.SearchBy = a["--by=".Length..]; break;
                case "--refresh": opt.Refresh = true; break;
                case "--upgrade":
                case "--sysupgrade": opt.Upgrade = true; break;
                case "--noconfirm": opt.NoConfirm = true; break;
                case "--needed": opt.Needed = true; break;
                case "--asdeps": opt.AsDeps = true; break;
                case "--review": opt.Review = true; break;
                case "--no-review": opt.NoReview = true; break;
                case "--recursive": opt.Recursive = true; break;
                case "--cascade": opt.Cascade = true; break;
                case "--nosave": opt.NoSave = true; break;
                case "--dry-run":
                case "-n":
                    opt.DryRun = true; break;
                case "--sudo-loop": opt.SudoLoop = true; break;
                case "--no-sudo-loop": opt.SudoLoop = false; break;
                case "--diff": opt.DiffReview = true; break;
                case "--all": opt.NewsAll = true; break;
                case "--limit":
                    if (++i >= args.Count) throw new ArgumentException("--limit requires a value");
                    if (!int.TryParse(args[i], out var n) || n < 0) throw new ArgumentException("--limit must be a non-negative integer");
                    opt.Limit = n; break;
                case var s when s.StartsWith("--limit=", StringComparison.Ordinal):
                    if (!int.TryParse(a["--limit=".Length..], out var n2) || n2 < 0) throw new ArgumentException("--limit must be a non-negative integer");
                    opt.Limit = n2; break;
                case "--since":
                    if (++i >= args.Count) throw new ArgumentException("--since requires a value");
                    if (!DateTimeOffset.TryParse(args[i], out var d)) throw new ArgumentException("--since: invalid date/time");
                    opt.NewsSince = d; break;
                case "--feed":
                    if (++i >= args.Count) throw new ArgumentException("--feed requires a value");
                    opt.NewsFeed = args[i]; break;
                case "--":
                    for (i++; i < args.Count; i++) opt.Positional.Add(args[i]);
                    break;
                default:
                    if (a.StartsWith("-", StringComparison.Ordinal) && a.Length > 1)
                        throw new ArgumentException($"unknown flag: {a}");
                    opt.Positional.Add(a);
                    break;
            }
        }
        return opt;
    }

    private static OutputFormat ParseFormat(string v) => v.ToLowerInvariant() switch
    {
        "auto" => OutputFormat.Auto,
        "table" => OutputFormat.Table,
        "json" => OutputFormat.Json,
        "ndjson" or "jsonl" => OutputFormat.Ndjson,
        "tsv" => OutputFormat.Tsv,
        "names" or "name" => OutputFormat.Names,
        "tssp" => OutputFormat.Tssp,
        _ => throw new ArgumentException($"unknown format: {v}"),
    };
}
