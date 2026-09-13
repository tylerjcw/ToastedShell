using Tosh.Runtime;

namespace Tosh.Crumb.Output;

/// <summary>
/// Crumb's help page, described as a <see cref="HelpTopic"/> and drawn by the shell's
/// own renderer.
/// </summary>
/// <remarks>
/// <para>
/// It used to be forty <c>Console.WriteLine</c> calls of hand-aligned columns, which is
/// a second help layout to maintain and looks like a different program from everything
/// else in the shell. Describing the command instead means `crumb --help` draws the same
/// panels `help ping` does, and the description is data — so it can be handed to
/// anything else that wants it.
/// </para>
/// </remarks>
internal static class CrumbHelp
{
    public static HelpTopic Topic() => new(
        Name: "crumb",
        Kind: HelpSubjectKind.External,
        Category: "Packages",
        Description: "TōSh's pacman + AUR companion. Speaks tables to a terminal and NDJSON to a pipe.",
        Usage: "crumb <subcommand> [options] [args...]\ncrumb -<OP><modifiers> [args...]",
        Aliases: Array.Empty<string>(),
        Related: ["pacman", "makepkg", "paru"],
        Examples: Array.Empty<string>(),
        Path: Environment.ProcessPath,
        Notes: PacmanForms,
        Arguments: null,
        Options: Options,
        PipelineInput: null,
        Output: "Package records. A TTY gets a coloured table; a pipe gets NDJSON, one Package per line.",
        ExampleItems: Examples,
        Streaming: null,
        Subcommands: Subcommands);

    private static readonly IReadOnlyList<HelpArgumentInfo> Subcommands =
    [
        new("search", "Union of repos and the AUR; ranked, structured."),
        new("info", "Detailed metadata for one or more packages."),
        new("list", "Installed packages, optionally filtered."),
        new("files", "Files owned by an installed package."),
        new("owns", "Which installed package owns a path."),
        new("install", "Repo via pacman, AUR via clone and makepkg."),
        new("install-file", "Install local package files, as pacman -U."),
        new("remove", "Remove packages, with --recursive and --nosave."),
        new("sync", "Refresh the package databases."),
        new("update", "Full system upgrade, and rebuild stale AUR packages."),
        new("clean", "Wipe the AUR build cache."),
        new("logs", "Inspect build logs."),
        new("gendb", "Seed the devel-commit cache for installed VCS packages."),
        new("news", "Arch Linux news headlines."),
    ];

    private static readonly IReadOnlyList<HelpOptionInfo> Options =
    [
        new("-q, --names", "Names only, one per line."),
        new("-v, --verbose", "Show extra fields."),
        new("-J, --json", "One JSON document."),
        new("-N, --ndjson", "One JSON object per line."),
        new("-T, --tsv", "Tab-separated values."),
        new("--format <fmt>", "auto, table, json, ndjson, tsv or names.", "auto"),
        new("--group-by <field>", "Group the install/remove summary by repo, source or version."),
        new("--limit <n>", "Cap results. Search trims to N by votes; news shows the N most recent."),

        // The three scopes read as one idea, in the order someone thinks of them.
        new("(default)", "Search both the repositories and the AUR."),
        new("--no-aur", "Repositories only. Alias: --repos, --repos-only."),
        new("--aur-only", "The AUR only. Alias: --aur."),
        new("--by <field>", "AUR search field: name-desc, name, maintainer, depends…", "name-desc"),

        new("--review", "Read each PKGBUILD before it builds. Also CRUMB_REVIEW=1."),
        new("--download-only", "Fetch without installing."),
        new("--dry-run", "Say what would happen, and do none of it."),
        new("--noconfirm", "Do not prompt."),
    ];

    private static readonly IReadOnlyList<HelpExample> Examples =
    [
        new("crumb -Ss dotnet", "Search repos and the AUR"),
        new("crumb search ripgrep --no-aur", "Repositories only"),
        new("crumb -SsJ dotnet | from json", "Structured records into a pipeline"),
        new("crumb -Qo /usr/bin/ls", "Which package owns a path"),
        new("crumb -S ripgrep", "Install from the repositories"),
        new("crumb install yay --review", "Read the PKGBUILD before building"),
        new("crumb -Syu", "Full system update"),
        new("crumb -Qt", "Orphans — installed as dependencies, wanted by nothing"),
        new("crumb logs --pkg yay --tail", "The newest build log for a package"),
    ];

    /// <remarks>
    /// The pacman spellings are a translation table rather than a list of options, so
    /// they sit in the notes: someone who knows pacman looks here once and then never
    /// again, while someone who does not never needs them at all.
    /// </remarks>
    private const string PacmanForms = """
        pacman-style forms — -S sync · -Q query · -R remove · -U install file · -F files

          -S <pkg>     install            -Q           list installed
          -Sy          refresh dbs        -Qs <term>   filter installed
          -Syu         full update        -Qi <pkg>    info on installed
          -Sw <pkg>    download only      -Ql <pkg>    files owned
          -Ss <terms>  search             -Qo <path>   owning package
          -Ssa <terms> search the AUR     -Qe          explicitly installed
          -Ssq <terms> search, names      -Qd          installed as dependencies
          -Si <pkg>    info               -Qm          foreign (AUR) packages
          -U <file>    install a file     -Qt          orphans
          -R <pkg>     remove             -Rs <pkg>    remove with orphaned deps

        Configuration is ~/.config/crumb/crumb.tosh — ToastScript whose value is a record.
        A flag beats an environment variable beats that file.

        Privilege escalation: $CRUMB_SUDO wins; otherwise doas, sudo, then pkexec.
        """;
}
