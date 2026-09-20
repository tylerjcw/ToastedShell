using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Crumb.Config;

/// <summary>
/// Settings read from <c>~/.config/crumb/crumb.tosh</c>.
/// </summary>
/// <remarks>
/// <para>
/// The file is ToastScript, evaluated by the real engine, and its value is a record:
/// </para>
/// <code>
/// # ~/.config/crumb/crumb.tosh
/// {|
///     quiet        = false
///     verbose      = false
///     pager        = "bat"
///     truecolor    = true
///     review       = true
///     exclude      = ["linux", "nvidia"]
///     makepkgFlags = ["--skippgpcheck"]
/// |}
/// </code>
/// <para>
/// Crumb is a TōSh companion, so its configuration is the language the user already
/// has rather than a format invented for the purpose. It also means a setting can be
/// *computed* — an exclude list built from a query, a pager chosen by what is
/// installed — which no declarative format allows.
/// </para>
/// <para>
/// Everything here was previously an environment variable. The file is the
/// lowest-priority source: a command-line flag beats an environment variable beats the
/// file beats the built-in default, so adding a file never takes an option away from
/// the command line.
/// </para>
/// <para>
/// Nothing is read unless the file exists, so the engine is never started for the
/// common case. A file that fails to evaluate is a warning, not an error — a package
/// manager that refuses to run because a preference file has a typo is worse than one
/// that says so and carries on with its defaults.
/// </para>
/// </remarks>
public sealed class CrumbConfig
{
    /// <summary>Default for <c>--quiet</c>.</summary>
    public bool? Quiet { get; init; }

    /// <summary>Default for <c>--verbose</c>.</summary>
    public bool? Verbose { get; init; }

    /// <summary>Pager for PKGBUILD review. Below <c>CRUMB_PAGER</c>, above <c>PAGER</c>.</summary>
    public string? Pager { get; init; }

    /// <summary>False disables 24-bit colour, as <c>CRUMB_NO_TRUECOLOR=1</c> does.</summary>
    public bool? Truecolor { get; init; }

    /// <summary>Default for <c>--review</c>.</summary>
    public bool? Review { get; init; }

    /// <summary>Package names to hold back from <c>-Syu</c>.</summary>
    public IReadOnlyList<string> Exclude { get; init; } = Array.Empty<string>();

    /// <summary>Extra arguments passed to every <c>makepkg</c> invocation.</summary>
    public IReadOnlyList<string> MakepkgFlags { get; init; } = Array.Empty<string>();

    /// <summary>The AUR endpoint, for a mirror or a test double.</summary>
    public string? AurBaseUrl { get; init; }

    /// <summary>The path the config is read from, honouring <c>XDG_CONFIG_HOME</c>.</summary>
    public static string DefaultPath
    {
        get
        {
            var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            var root = !string.IsNullOrEmpty(xdg)
                ? xdg!
                : Path.Combine(Environment.GetEnvironmentVariable("HOME") ?? ".", ".config");
            return Path.Combine(root, "crumb", "crumb.tosh");
        }
    }

    private static CrumbConfig? _cached;

    /// <summary>The config for this run, read once.</summary>
    public static CrumbConfig Current => _cached ??= Load(DefaultPath);

    /// <summary>Forgets the cached config. For tests.</summary>
    internal static void Reset() => _cached = null;

    public static CrumbConfig Load(string path)
    {
        // The engine is not started when there is nothing to read, which is the usual
        // case — `crumb -Q` should not pay for a feature nobody configured.
        if (!File.Exists(path)) return new CrumbConfig();

        try
        {
            return FromRecord(Evaluate(File.ReadAllText(path)));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"crumb: warning: cannot read {path}: {ex.Message}");
            return new CrumbConfig();
        }
    }

    /// <summary>Evaluates a config script and returns the record it produced, if any.</summary>
    internal static IDictionary<string, object?>? Evaluate(string source)
    {
        // A bare language runtime, not the full shell. A config file is expressions —
        // records, literals, conditionals, string interpolation — and keeping the
        // built-in command set out of it means the engine Crumb carries is the language
        // only, and a config file cannot quietly shell out during start-up.
        var engine = new ToshEngine(new ToastRuntime());

        // The script's value is its configuration. A record literal evaluates to an
        // ExpandoObject, which is an IDictionary — nothing further is needed to read it.
        var results = engine.ExecuteToListAsync(source).GetAwaiter().GetResult();
        return results.LastOrDefault() as IDictionary<string, object?>;
    }

    internal static CrumbConfig FromRecord(IDictionary<string, object?>? record)
    {
        if (record is null) return new CrumbConfig();

        return new CrumbConfig
        {
            Quiet = Bool(record, "quiet"),
            Verbose = Bool(record, "verbose"),
            Pager = Text(record, "pager"),
            Truecolor = Bool(record, "truecolor"),
            Review = Bool(record, "review"),
            Exclude = Strings(record, "exclude"),
            MakepkgFlags = Strings(record, "makepkgFlags"),
            AurBaseUrl = Text(record, "aurBaseUrl"),
        };
    }

    /// <remarks>
    /// Field names are matched without regard to case, because a record written
    /// <c>Pager</c> is the same intent as one written <c>pager</c> and failing silently
    /// over a capital letter is a poor way to learn that.
    /// </remarks>
    private static bool TryGet(IDictionary<string, object?> record, string key, out object? value)
    {
        if (record.TryGetValue(key, out value)) return true;
        foreach (var (k, v) in record)
        {
            if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
            {
                value = v;
                return true;
            }
        }
        value = null;
        return false;
    }

    private static bool? Bool(IDictionary<string, object?> record, string key)
        => TryGet(record, key, out var v) && v is bool b ? b : null;

    private static string? Text(IDictionary<string, object?> record, string key)
        => TryGet(record, key, out var v) && v is not null && v.ToString() is { Length: > 0 } s ? s : null;

    private static IReadOnlyList<string> Strings(IDictionary<string, object?> record, string key)
    {
        if (!TryGet(record, key, out var v) || v is null) return Array.Empty<string>();

        // A single value is accepted where a list is expected: `exclude = "linux"` reads
        // the way someone means it, and refusing it teaches nothing.
        if (v is string one) return new[] { one };

        if (v is System.Collections.IEnumerable items)
        {
            var list = new List<string>();
            foreach (var item in items)
                if (item?.ToString() is { Length: > 0 } text) list.Add(text);
            return list;
        }

        return Array.Empty<string>();
    }

    /// <summary>
    /// The pager to use, in precedence order: an explicit <c>--pager</c>, then
    /// <c>CRUMB_PAGER</c>, then the config file, then <c>PAGER</c>, then <c>less</c>.
    /// </summary>
    /// <remarks>
    /// The file sits above <c>PAGER</c> and below <c>CRUMB_PAGER</c> deliberately:
    /// <c>PAGER</c> is a system-wide default someone set years ago, the file is a choice
    /// made about Crumb, and <c>CRUMB_PAGER</c> is a choice made about this run.
    /// </remarks>
    public static string ResolvePager(string? explicitPager) =>
        explicitPager
        ?? Environment.GetEnvironmentVariable("CRUMB_PAGER")
        ?? (string.IsNullOrEmpty(Current.Pager) ? null : Current.Pager)
        ?? Environment.GetEnvironmentVariable("PAGER")
        ?? "less";

    /// <summary>
    /// The AUR endpoint, in precedence order: an explicit <c>--aur-base-url</c>, then
    /// <c>CRUMB_AUR_BASE_URL</c>, then the config file, then the real AUR.
    /// </summary>
    /// <remarks>
    /// The same ordering as <see cref="ResolvePager"/> and for the same reason: the file is
    /// a choice made about Crumb, the environment variable is a choice made about this run,
    /// and the flag is a choice made about this command. There is no system-wide default to
    /// sit below the file, so the chain is one shorter.
    /// </remarks>
    public static string ResolveAurBaseUrl(string? explicitBaseUrl) =>
        explicitBaseUrl
        ?? Environment.GetEnvironmentVariable("CRUMB_AUR_BASE_URL")
        ?? (string.IsNullOrEmpty(Current.AurBaseUrl) ? null : Current.AurBaseUrl)
        ?? Aur.AurClient.DefaultBaseUrl;

    /// <summary>
    /// The endpoint an <c>AurClient</c> built without one uses — <c>CRUMB-0001</c>.
    /// </summary>
    /// <remarks>
    /// A process-wide value because the alternative is threading a string through four
    /// call chains that have no other reason to know about it: of the five places an
    /// <c>AurClient</c> is built, one has the parsed options in scope. Set once from the
    /// command line before any work starts, exactly as <see cref="Current"/> is read once.
    /// </remarks>
    public static string? AurBaseUrlOverride { get; set; }
}
