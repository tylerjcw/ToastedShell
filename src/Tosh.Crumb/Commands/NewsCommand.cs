using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;
using Tosh.Crumb.Output;

namespace Tosh.Crumb.Commands;

/// <summary>
/// Mirrors paru's <c>news.rs</c>: fetches the Arch Linux news feed
/// (<c>https://archlinux.org/feeds/news/</c>), parses the RSS, and
/// prints the most recent items in reverse-chronological order with
/// minimal HTML decoded.
/// </summary>
public static class NewsCommand
{
    private const string DefaultFeed = "https://archlinux.org/feeds/news/";

    public static async Task<int> RunAsync(CrumbOptions opt, CancellationToken ct)
    {
        var limit = opt.Limit ?? 5;
        DateTimeOffset? cutoff = opt.NewsSince;
        // By default, only show news newer than the most recent build
        // date among installed packages — that's the rough cut-off for
        // "things the user hasn't applied yet". This matches paru's
        // `newest_pkg(config)` heuristic.
        if (cutoff is null && !opt.NewsAll)
        {
            cutoff = NewestInstalledBuildDate();
        }

        string xml;
        try
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd("crumb/0.1 (+https://github.com/komradbobo/tosh)");
            http.Timeout = TimeSpan.FromSeconds(20);
            xml = await http.GetStringAsync(opt.NewsFeed ?? DefaultFeed, ct);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"crumb news: cannot fetch feed: {ex.Message}");
            return 1;
        }

        XDocument doc;
        try { doc = XDocument.Parse(xml); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"crumb news: cannot parse feed: {ex.Message}");
            return 1;
        }

        var items = doc.Descendants("item")
            .Select(ParseItem)
            .Where(it => it is not null)
            .Cast<NewsItem>()
            .OrderByDescending(it => it.Date)
            .ToList();

        var filtered = cutoff is { } c
            ? items.Where(it => it.Date >= c).ToList()
            : items;
        if (opt.NewsAll) filtered = items;
        if (limit > 0) filtered = filtered.Take(limit).ToList();

        if (filtered.Count == 0)
        {
            Confirm.Status("no new news");
            return 1;
        }

        var first = true;
        foreach (var item in filtered)
        {
            if (!first) Console.WriteLine();
            first = false;
            Console.WriteLine($"{item.Date:yyyy-MM-dd}  {item.Title}");
            foreach (var line in WrapBody(item.Body))
                Console.WriteLine("    " + line);
        }
        return 0;
    }

    private static NewsItem? ParseItem(XElement el)
    {
        var title = el.Element("title")?.Value?.Trim();
        var pub = el.Element("pubDate")?.Value;
        var desc = el.Element("description")?.Value ?? string.Empty;
        if (string.IsNullOrEmpty(title)) return null;
        DateTimeOffset.TryParse(pub, out var dt);
        return new NewsItem(title!, dt, StripHtml(desc));
    }

    private static DateTimeOffset? NewestInstalledBuildDate()
    {
        try
        {
            var db = new Pacman.PacmanDb();
            return db.Local.Values
                .Select(p => p.BuildDate)
                .Where(d => d is not null)
                .Max();
        }
        catch { return null; }
    }

    /// <summary>
    /// Minimal HTML stripper: drops tags, decodes the handful of
    /// entities Arch's news feed actually uses, collapses whitespace
    /// while preserving paragraph breaks.
    /// </summary>
    private static string StripHtml(string html)
    {
        var sb = new StringBuilder(html.Length);
        var inTag = false;
        foreach (var ch in html)
        {
            if (ch == '<') { inTag = true; continue; }
            if (ch == '>') { inTag = false; sb.Append(' '); continue; }
            if (!inTag) sb.Append(ch);
        }
        var text = sb.ToString()
            .Replace("&amp;", "&")
            .Replace("&lt;", "<")
            .Replace("&gt;", ">")
            .Replace("&quot;", "\"")
            .Replace("&#39;", "'")
            .Replace("&nbsp;", " ");
        // Normalise whitespace but keep double-newline paragraph breaks.
        var paras = text.Split(new[] { "\n\n", "\r\n\r\n" }, StringSplitOptions.None);
        var cleaned = paras
            .Select(p => string.Join(' ',
                p.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)))
            .Where(p => p.Length > 0);
        return string.Join("\n\n", cleaned);
    }

    private static IEnumerable<string> WrapBody(string text, int width = 76)
    {
        foreach (var para in text.Split("\n\n"))
        {
            if (para.Length == 0) { yield return string.Empty; continue; }
            var line = new StringBuilder();
            foreach (var word in para.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.Length > 0 && line.Length + 1 + word.Length > width)
                {
                    yield return line.ToString();
                    line.Clear();
                }
                if (line.Length > 0) line.Append(' ');
                line.Append(word);
            }
            if (line.Length > 0) yield return line.ToString();
            yield return string.Empty;
        }
    }

    private sealed record NewsItem(string Title, DateTimeOffset Date, string Body);
}
