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
        var text = System.IO.File.ReadAllText("/home/komrad/projects/tosh/scripts/build.tosh");

        Probe1("subcommand version", "subcommand ", text, f);
        Probe1("subcommand publish", "subcommand ", text, f);
        Probe1("flag testFlag", "flag ", text, f);
        Probe1("arg testArg", "arg ", text, f);
        Probe1("type NonEmptyString", "type ", text, f);
    }

    private void Probe1(string needle, string prefix, string text, ToshLanguageFeatures f)
    {
        var idx = text.IndexOf(needle);
        var pre = text.Substring(0, idx).Split('\n');
        int line = pre.Length - 1;
        int col = pre[^1].Length + prefix.Length + 2;
        var h = f.GetHover(text, "build.tosh", new LspPosition(line, col));
        _out.WriteLine($"=== {needle} (L{line} C{col}) ===");
        _out.WriteLine(h?.Contents.Value ?? "<null>");
        _out.WriteLine("");
    }
}
