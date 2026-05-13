using System.Text;
using System.Text.Json;
using Tosh.Crumb.Models;

namespace Tosh.Crumb.Output;

public enum OutputFormat { Auto, Table, Json, Ndjson, Tsv, Names, Tssp }

/// <summary>
/// Renders Package streams. When stdout is a TTY → coloured pretty table.
/// When piped → TSSP frames by default if ToSh negotiated it
/// (<c>TOSH_STRUCTURED_STDOUT=1</c> + consumer is <c>pipe</c>/<c>capture</c>),
/// otherwise NDJSON so `crumb search foo | from json` still works in plain
/// shells. Explicit <c>--format</c> overrides the detection.
/// </summary>
public static class PackageFormatter
{
    public static OutputFormat Resolve(OutputFormat requested)
    {
        if (requested != OutputFormat.Auto) return requested;
        if (Console.IsOutputRedirected)
        {
            var negotiated = Environment.GetEnvironmentVariable("TOSH_STRUCTURED_STDOUT") == "1";
            if (negotiated) return OutputFormat.Tssp;
            return OutputFormat.Ndjson;
        }
        return OutputFormat.Table;
    }

    public static int Render(IEnumerable<Package> packages, OutputFormat format, bool verbose = false)
    {
        format = Resolve(format);
        return format switch
        {
            OutputFormat.Json => RenderJson(packages),
            OutputFormat.Ndjson => RenderNdjson(packages),
            OutputFormat.Tssp => RenderTssp(packages),
            OutputFormat.Tsv => RenderTsv(packages),
            OutputFormat.Names => RenderNames(packages),
            _ => RenderTable(packages, verbose),
        };
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly JsonSerializerOptions JsonOptsPretty = new(JsonOpts) { WriteIndented = true };

    private static int RenderJson(IEnumerable<Package> packages)
    {
        var list = packages.ToList();
        Console.WriteLine(JsonSerializer.Serialize(list, JsonOptsPretty));
        return list.Count == 0 ? 1 : 0;
    }

    private static int RenderNdjson(IEnumerable<Package> packages)
    {
        var count = 0;
        foreach (var p in packages)
        {
            Console.WriteLine(JsonSerializer.Serialize(p, JsonOpts));
            count++;
        }
        return count == 0 ? 1 : 0;
    }

    private static int RenderTssp(IEnumerable<Package> packages)
    {
        using var stdout = Console.OpenStandardOutput();
        var count = WriteTssp(stdout, packages);
        stdout.Flush();
        return count == 0 ? 1 : 0;
    }

    // Schema for crumb.package — emitted in a meta frame so downstream
    // consumers get type hints and column ordering without hard-coding them.
    // The rec frames carry a display-friendly projection (DisplayPackage),
    // so size/date fields are pre-formatted strings here.
    internal const string CrumbPackageSchemaJson = """
        {"schema":"crumb.package","title":"{Name} | {Version}","fields":{"Status":{"type":"string","enum":["\u2713","\u26A0","\u2717"]},"Repo":{"type":"string","enum":["core","extra","multilib","aur","local"]},"Name":{"type":"string"},"Version":{"type":"string"},"Description":{"type":"string","nullable":true},"InstalledVersion":{"type":"string","nullable":true},"InstallReason":{"type":"string","enum":["explicit","depend"],"nullable":true},"Votes":{"type":"integer","nullable":true},"Popularity":{"type":"number","nullable":true},"OutOfDate":{"type":"string","nullable":true},"BuildDate":{"type":"string","nullable":true},"DownloadSize":{"type":"string","nullable":true},"InstalledSize":{"type":"string","nullable":true},"Url":{"type":"string","nullable":true},"Packager":{"type":"string","nullable":true},"Architecture":{"type":"string","nullable":true},"License":{"type":"string","nullable":true},"Base":{"type":"string","nullable":true},"Groups":{"type":"array","nullable":true},"Depends":{"type":"array","nullable":true},"MakeDepends":{"type":"array","nullable":true},"CheckDepends":{"type":"array","nullable":true},"OptDepends":{"type":"array","nullable":true},"Provides":{"type":"array","nullable":true},"Conflicts":{"type":"array","nullable":true},"Replaces":{"type":"array","nullable":true},"InstallDate":{"type":"string","nullable":true},"FirstSubmitted":{"type":"string","nullable":true},"LastModified":{"type":"string","nullable":true}}}
        """;

    /// <summary>Writes a complete TSSP stream (header + meta + rec frames)
    /// to <paramref name="output"/>. Returns the number of <c>rec</c>
    /// frames emitted. Exposed for testing and for re-use by other
    /// command paths that already own a Stream.</summary>
    internal static int WriteTssp(Stream output, IEnumerable<Package> packages)
    {
        using var writer = new Tosh.Client.ToshFrameWriter(output, leaveOpen: true);
        writer.WriteHeader("crumb.package");
        writer.WriteMeta(CrumbPackageSchemaJson);

        var count = 0;
        foreach (var p in packages)
        {
            var display = DisplayPackage.From(p);
            writer.WriteRecord(display, JsonOpts);
            count++;
        }
        return count;
    }

    private static int RenderTsv(IEnumerable<Package> packages)
    {
        Console.WriteLine("Repo\tName\tVersion\tInstalled\tVotes\tDescription");
        var count = 0;
        foreach (var p in packages)
        {
            Console.WriteLine(string.Join('\t',
                p.Repo,
                p.Name,
                p.Version,
                p.Installed ? p.InstalledVersion ?? "yes" : "",
                p.Votes?.ToString() ?? "",
                (p.Description ?? string.Empty).Replace('\t', ' ')));
            count++;
        }
        return count == 0 ? 1 : 0;
    }

    private static int RenderNames(IEnumerable<Package> packages)
    {
        var count = 0;
        foreach (var p in packages) { Console.WriteLine(p.Name); count++; }
        return count == 0 ? 1 : 0;
    }

    // ─── Pretty table ──────────────────────────────────────────────
    // Two-line per package layout:
    //   <repo>/<name> <version> [votes] [installed]
    //       <description>

    private static int RenderTable(IEnumerable<Package> packages, bool verbose)
    {
        var truecolor = ColorSupport.SupportsTrueColor();
        var count = 0;
        var sb = new StringBuilder();
        foreach (var p in packages)
        {
            sb.Clear();
            sb.Append(Color(p.Repo, RepoColor(p.Repo), truecolor));
            sb.Append('/');
            sb.Append(Color(p.Name, NameColor, truecolor));
            sb.Append(' ');
            sb.Append(Color(p.Version, VersionColor, truecolor));

            if (p.Votes is int v)
            {
                sb.Append(' ');
                sb.Append(Color($"(+{v} {p.Popularity:0.00})", VotesColor, truecolor));
            }
            if (p.OutOfDate is { } ood)
            {
                sb.Append(' ');
                sb.Append(Color($"[out-of-date {ood:yyyy-MM-dd}]", WarnColor, truecolor));
            }
            if (p.Installed)
            {
                sb.Append(' ');
                var tag = p.InstalledVersion is { } iv && iv != p.Version
                    ? $"[installed: {iv}]"
                    : "[installed]";
                sb.Append(Color(tag, InstalledColor, truecolor));
            }

            Console.WriteLine(sb);
            if (!string.IsNullOrEmpty(p.Description))
                Console.WriteLine("    " + p.Description);

            if (verbose)
            {
                if (p.Maintainer is not null) Console.WriteLine($"    maintainer: {p.Maintainer}");
                if (p.Url is not null) Console.WriteLine($"    url:        {p.Url}");
                if (p.Depends.Count > 0) Console.WriteLine($"    depends:    {string.Join(' ', p.Depends)}");
            }
            count++;
        }
        return count == 0 ? 1 : 0;
    }

    // Roughly aligned with Tome's BuiltinDark palette so colours feel
    // native when crumb runs alongside the rest of the toolkit. We avoid
    // depending on Tosh.Tome directly because Role/TomeTheme are internal.
    private const string Reset = "\u001b[0m";

    private static readonly (byte r, byte g, byte b, byte idx) NameColor = (0x87, 0xD7, 0xFF, 117);
    private static readonly (byte r, byte g, byte b, byte idx) VersionColor = (0x87, 0xFF, 0x87, 120);
    private static readonly (byte r, byte g, byte b, byte idx) VotesColor = (0xAF, 0xAF, 0xAF, 145);
    private static readonly (byte r, byte g, byte b, byte idx) InstalledColor = (0x5F, 0xD7, 0xAF, 79);
    private static readonly (byte r, byte g, byte b, byte idx) WarnColor = (0xFF, 0x87, 0x5F, 209);

    private static (byte r, byte g, byte b, byte idx) RepoColor(string repo) => repo switch
    {
        "core" => (0xFF, 0xD7, 0x5F, 221), // amber
        "extra" => (0x87, 0xAF, 0xFF, 111), // soft blue
        "multilib" => (0xAF, 0x87, 0xFF, 141), // purple
        "aur" => (0x5F, 0xD7, 0xFF, 81), // cyan
        "local" => (0xAF, 0xAF, 0xAF, 145), // dim
        _ => (0xD7, 0xAF, 0x87, 180), // tan
    };

    private static string Color(string text, (byte r, byte g, byte b, byte idx) c, bool truecolor)
    {
        if (!ColorSupport.StdoutColorEnabled()) return text;
        var open = truecolor
            ? $"\u001b[38;2;{c.r};{c.g};{c.b}m"
            : $"\u001b[38;5;{c.idx}m";
        return open + text + Reset;
    }
}

/// <summary>Display-friendly projection of <see cref="Package"/> emitted in
/// TSSP <c>rec</c> frames. Sizes are human-readable strings (e.g. "162 kB"),
/// dates are local-time ISO strings, and a single <c>Status</c> glyph
/// summarises install state: ✓ current, ⚠ outdated, ✗ not installed.</summary>
internal sealed record DisplayPackage(
    string Status,
    string Repo,
    string Name,
    string Version,
    string? Description,
    string? InstalledVersion,
    string? InstallReason,
    int? Votes,
    double? Popularity,
    string? OutOfDate,
    string? BuildDate,
    string? DownloadSize,
    string? InstalledSize,
    string? Url,
    string? Packager,
    string? Architecture,
    string? License,
    string? Base,
    IReadOnlyList<string>? Groups,
    IReadOnlyList<string>? Depends,
    IReadOnlyList<string>? MakeDepends,
    IReadOnlyList<string>? CheckDepends,
    IReadOnlyList<string>? OptDepends,
    IReadOnlyList<string>? Provides,
    IReadOnlyList<string>? Conflicts,
    IReadOnlyList<string>? Replaces,
    string? InstallDate,
    string? FirstSubmitted,
    string? LastModified)
{
    public static DisplayPackage From(Package p) => new(
        Status: PackageDisplayUtilities.StatusGlyph(p),
        Repo: p.Repo,
        Name: p.Name,
        Version: p.Version,
        Description: p.Description,
        InstalledVersion: p.InstalledVersion,
        InstallReason: p.InstallReason,
        Votes: p.Votes,
        Popularity: p.Popularity,
        OutOfDate: PackageDisplayUtilities.FormatLocal(p.OutOfDate),
        BuildDate: PackageDisplayUtilities.FormatLocal(p.BuildDate),
        DownloadSize: PackageDisplayUtilities.FormatBytes(p.DownloadSize),
        InstalledSize: PackageDisplayUtilities.FormatBytes(p.InstalledSize),
        Url: p.Url,
        Packager: p.Packager,
        Architecture: p.Architecture,
        License: p.License,
        Base: p.Base,
        Groups: NullIfEmpty(p.Groups),
        Depends: NullIfEmpty(p.Depends),
        MakeDepends: NullIfEmpty(p.MakeDepends),
        CheckDepends: NullIfEmpty(p.CheckDepends),
        OptDepends: NullIfEmpty(p.OptDepends),
        Provides: NullIfEmpty(p.Provides),
        Conflicts: NullIfEmpty(p.Conflicts),
        Replaces: NullIfEmpty(p.Replaces),
        InstallDate: PackageDisplayUtilities.FormatLocal(p.InstallDate),
        FirstSubmitted: PackageDisplayUtilities.FormatLocal(p.FirstSubmitted),
        LastModified: PackageDisplayUtilities.FormatLocal(p.LastModified));

    private static IReadOnlyList<string>? NullIfEmpty(IReadOnlyList<string> list)
        => list is null || list.Count == 0 ? null : list;
}

internal static class PackageDisplayUtilities
{
    /// <summary>✓ installed at current version · ⚠ installed but outdated · ✗ not installed.</summary>
    public static string StatusGlyph(Package p)
    {
        if (!p.Installed) return "\u2717"; // ✗
        if (p.InstalledVersion is { } iv && !string.Equals(iv, p.Version, StringComparison.Ordinal))
            return "\u26A0"; // ⚠
        return "\u2713"; // ✓
    }

    public static string? FormatLocal(DateTimeOffset? value)
        => value is null ? null : value.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

    /// <summary>Format a byte count using SI multiples (kB, MB, GB, TB) with
    /// one decimal place above 9.9. Returns null when the input is null;
    /// returns "0 B" for zero. Matches the style ToSh uses elsewhere.</summary>
    public static string? FormatBytes(long? bytes)
    {
        if (bytes is null) return null;
        var v = bytes.Value;
        if (v < 0) return v.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (v < 1000) return $"{v} B";
        string[] units = ["kB", "MB", "GB", "TB", "PB"];
        double d = v;
        var u = -1;
        do { d /= 1000.0; u++; } while (d >= 1000 && u < units.Length - 1);
        var fmt = d < 10 ? "0.00" : d < 100 ? "0.0" : "0";
        return d.ToString(fmt, System.Globalization.CultureInfo.InvariantCulture) + " " + units[u];
    }
}
