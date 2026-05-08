using Tosh.LanguageServices;
using Xunit;
using Xunit.Abstractions;

namespace Tosh.Tests;

public class _HoverProbe(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _out = output;

    [Fact]
    public void Probe()
    {
        var f = new ToshLanguageFeatures();
        var text = File.ReadAllText(LocateBuildScript());

        Probe1("subcommand version", "subcommand ", text, f);
        Probe1("subcommand publish", "subcommand ", text, f);
        Probe1("flag year", "flag ", text, f);
        Probe1("arg targets", "arg ", text, f);
        Probe1("type NonEmptyString", "type ", text, f);
    }

    private void Probe1(string needle, string prefix, string text, ToshLanguageFeatures f)
    {
        var idx = text.IndexOf(needle, StringComparison.Ordinal);
        Assert.True(idx >= 0, $"Could not find '{needle}' in scripts/build.tosh.");

        var pre = text.Substring(0, idx).Split('\n');
        int line = pre.Length - 1;
        int col = pre[^1].Length + prefix.Length + 1;
        var h = f.GetHover(text, "build.tosh", new LspPosition(line, col));
        _out.WriteLine($"=== {needle} (L{line} C{col}) ===");
        _out.WriteLine(h?.Contents.Value ?? "<null>");
        _out.WriteLine("");
        Assert.NotNull(h);
    }

    private static string LocateBuildScript()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "scripts", "build.tosh");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException("Could not locate scripts/build.tosh relative to repo root.");
    }
}
