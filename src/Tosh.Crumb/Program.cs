using Tosh.Crumb.Commands;

namespace Tosh.Crumb;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintHelp();
            return args.Length == 0 ? 1 : 0;
        }
        if (args[0] is "-V" or "--version" or "version")
        {
            Console.WriteLine("crumb 0.1.0");
            return 0;
        }

        // crumb leans on $HOME for the AUR clone cache, devel tracker,
        // and review history. Validate up-front so a missing $HOME
        // surfaces as a one-line error instead of a stack trace from
        // deep inside an AurBuilder property getter.
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HOME"))
            && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("XDG_CACHE_HOME")))
        {
            Console.Error.WriteLine("crumb: $HOME is not set (and no XDG_CACHE_HOME fallback)");
            Console.Error.WriteLine("       crumb needs a home directory for its build/devel cache.");
            return 2;
        }

        string subcommand;
        string[] rest;

        try
        {
            var expansion = PacmanFlags.TryExpand(args[0]);
            if (expansion is not null)
            {
                subcommand = expansion.Subcommand;
                rest = expansion.InjectedFlags.Concat(args.Skip(1)).ToArray();
            }
            else
            {
                subcommand = args[0];
                rest = args.Skip(1).ToArray();
            }

            var opt = CrumbOptions.Parse(rest);
            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

            return subcommand switch
            {
                "search" or "s" => await CrumbCommands.SearchAsync(opt, cts.Token),
                "info" or "show" or "i" => await CrumbCommands.InfoAsync(opt, cts.Token),
                "list" or "ls" or "l" => CrumbCommands.List(opt),
                "files" or "fl" => CrumbCommands.Files(opt),
                "owns" or "own" or "o" => CrumbCommands.Owns(opt),
                "install" or "in" or "add" => await CrumbCommands.InstallAsync(opt, cts.Token),
                "remove" or "rm" or "uninstall" => await CrumbCommands.RemoveAsync(opt, cts.Token),
                "sync" or "sy" or "refresh" => await CrumbCommands.SyncAsync(opt, cts.Token),
                "update" or "up" or "upgrade" => await CrumbCommands.UpdateAsync(opt, cts.Token),
                "clean" or "purge" => await CrumbCommands.CleanAsync(opt, cts.Token),
                "gendb" => await CrumbCommands.GenDbAsync(opt, cts.Token),
                "news" => await NewsCommand.RunAsync(opt, cts.Token),
                _ => UnknownCommand(subcommand),
            };
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"crumb: {ex.Message}");
            return 2;
        }
        catch (OperationCanceledException)
        {
            return 130;
        }
    }

    private static int UnknownCommand(string name)
    {
        Console.Error.WriteLine($"crumb: unknown subcommand '{name}'");
        Console.Error.WriteLine("       run `crumb help` for the list.");
        return 2;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("crumb — TōSh's pacman + AUR companion");
        Console.WriteLine();
        Console.WriteLine("usage:");
        Console.WriteLine("  crumb <subcommand> [options] [args...]");
        Console.WriteLine("  crumb -<OP><modifiers> [args...]    pacman-style stacked flags");
        Console.WriteLine();
        Console.WriteLine("pacman-style operations:");
        Console.WriteLine("  -S    sync (install / refresh / upgrade)   -Q    query installed");
        Console.WriteLine("  -R    remove                               -F    sync file index");
        Console.WriteLine();
        Console.WriteLine("common stacked combos:");
        Console.WriteLine("  -S <pkg>      install                          (= install)");
        Console.WriteLine("  -Sy           refresh package databases        (= sync)");
        Console.WriteLine("  -Syu          full system update               (= update)");
        Console.WriteLine("  -Ss <terms>   search repos + AUR                (= search)");
        Console.WriteLine("  -Si <pkg>     info on a repo/AUR pkg            (= info)");
        Console.WriteLine("  -Ssa <terms>  AUR-only search                   (= search --aur-only)");
        Console.WriteLine("  -Ssq <terms>  search, names only                (= search --names)");
        Console.WriteLine("  -SsJ <terms>  search, JSON output               (= search --json)");
        Console.WriteLine("  -R <pkg>      remove a package                  (= remove)");
        Console.WriteLine("  -Rs <pkg>     remove + orphaned deps            (= remove --recursive)");
        Console.WriteLine("  -Rn <pkg>     remove, skip .pacsave backups     (= remove --nosave)");
        Console.WriteLine("  -Q            list everything installed         (= list)");
        Console.WriteLine("  -Qs <term>    filter installed                  (= list <term>)");
        Console.WriteLine("  -Qi <pkg>     info on installed pkg             (= info --installed)");
        Console.WriteLine("  -Ql <pkg>     files owned by pkg                (= files)");
        Console.WriteLine("  -Qo <path>    which pkg owns path               (= owns)");
        Console.WriteLine("  -Qe           explicitly installed              (= list --explicit)");
        Console.WriteLine("  -Qm           foreign packages                  (= list --foreign)");
        Console.WriteLine("  -Qt           orphans                           (= list --orphans)");
        Console.WriteLine();
        Console.WriteLine("subcommands (long-form, equivalent to the above):");
        Console.WriteLine("  search   <terms...>   union of repos + AUR; ranked, structured");
        Console.WriteLine("  info     <pkg...>     detailed metadata for one or more packages");
        Console.WriteLine("  list     [filter...]  installed packages, optionally filtered");
        Console.WriteLine("  files    <pkg>        files owned by an installed package");
        Console.WriteLine("  owns     <path>       which installed package owns a path");
        Console.WriteLine("  install  <pkg...>     repo via pacman, AUR via clone + makepkg -si");
        Console.WriteLine("  remove   <pkg...>     pacman -R (with --recursive / --nosave)");
        Console.WriteLine("  sync                  pacman -Sy (refresh databases)");
        Console.WriteLine("  update                pacman -Syu + rebuild stale AUR packages");
        Console.WriteLine("  clean                 wipe the AUR build cache (~/.cache/crumb/aur)");
        Console.WriteLine("  gendb                 seed the devel-commit cache for installed VCS pkgs");
        Console.WriteLine("  news [--all] [--limit N] [--since DATE]   Arch Linux news headlines");
        Console.WriteLine();
        Console.WriteLine("output modifiers (work with both forms):");
        Console.WriteLine("  -q / --names       just names, one per line");
        Console.WriteLine("  -v / --verbose     show extra fields");
        Console.WriteLine("  -J / --json        single JSON document");
        Console.WriteLine("  -N / --ndjson      one JSON object per line");
        Console.WriteLine("  -T / --tsv         tab-separated values");
        Console.WriteLine("  --format <fmt>     auto | table | json | ndjson | tsv | names");
        Console.WriteLine();
        Console.WriteLine("scope filters:");
        Console.WriteLine("  --repos / -Sr*     sync repos only");
        Console.WriteLine("  --aur   / -Ss*a    AUR only");
        Console.WriteLine("  --by <field>       AUR search field (name-desc, name, maintainer, depends, …)");
        Console.WriteLine("  --limit N          cap results: search trims to N (AUR ranked by votes); news shows N most recent");
        Console.WriteLine();
        Console.WriteLine("default output:");
        Console.WriteLine("  When stdout is a TTY  → pretty coloured table");
        Console.WriteLine("  When piped            → NDJSON (one Package per line)");
        Console.WriteLine();
        Console.WriteLine("examples:");
        Console.WriteLine("  crumb -Ss dotnet                                # pretty terminal output");
        Console.WriteLine("  crumb -Ssa wlroots                              # AUR-only search");
        Console.WriteLine("  crumb -SsJ dotnet | from json                   # structured records");
        Console.WriteLine("  crumb -Qq | wc -l                               # count installed pkgs");
        Console.WriteLine("  crumb -Qo /usr/bin/ls                           # owning package");
        Console.WriteLine("  crumb -S ripgrep                                # install from repos");
        Console.WriteLine("  crumb install yay --review                      # AUR: review PKGBUILD before build");
        Console.WriteLine("  crumb install yay                               # AUR: build without review (default)");
        Console.WriteLine("  crumb -Syu                                      # full system update");
        Console.WriteLine("  crumb update --aur                              # AUR rebuilds only");
        Console.WriteLine("  crumb -Rsn old-pkg                              # remove with deps, no backups");
        Console.WriteLine();
        Console.WriteLine("privilege escalation:");
        Console.WriteLine("  $CRUMB_SUDO env wins; otherwise doas → sudo → pkexec are auto-detected.");
        Console.WriteLine();
        Console.WriteLine("AUR review:");
        Console.WriteLine("  PKGBUILD review is OFF by default (paru-style).");
        Console.WriteLine("  Use --review or set CRUMB_REVIEW=1 to enable; reviews are batched up front.");
    }
}
