using Tosh.Crumb.Commands;

namespace Tosh.Tests;

public class CrumbPacmanFlagsTests
{
    [Theory]
    [InlineData("-Ss", "search", new string[0])]
    [InlineData("-Ssa", "search", new[] { "--aur-only" })]
    [InlineData("-Ssr", "search", new[] { "--repos-only" })]
    [InlineData("-Ssq", "search", new[] { "--names" })]
    [InlineData("-SsJ", "search", new[] { "--json" })]
    [InlineData("-SsN", "search", new[] { "--ndjson" })]
    [InlineData("-Si", "info", new string[0])]
    [InlineData("-S", "install", new string[0])]
    [InlineData("-Sw", "install", new[] { "--download-only" })]
    [InlineData("-Syw", "install", new[] { "--refresh", "--download-only" })]
    [InlineData("-Sy", "sync", new[] { "--refresh" })]
    [InlineData("-Su", "update", new[] { "--upgrade" })]
    [InlineData("-Syu", "update", new[] { "--refresh", "--upgrade" })]
    [InlineData("-Suw", "update", new[] { "--upgrade", "--download-only" })]
    [InlineData("-Syuw", "update", new[] { "--refresh", "--upgrade", "--download-only" })]
    [InlineData("-U", "install-file", new string[0])]
    [InlineData("-Q", "list", new string[0])]
    [InlineData("-Qe", "list", new[] { "--explicit" })]
    [InlineData("-Qm", "list", new[] { "--foreign" })]
    [InlineData("-Qt", "list", new[] { "--orphans" })]
    [InlineData("-Qi", "info", new[] { "--installed" })]
    [InlineData("-Ql", "files", new string[0])]
    [InlineData("-Qo", "owns", new string[0])]
    [InlineData("-Qq", "list", new[] { "--names" })]
    [InlineData("-R", "remove", new string[0])]
    [InlineData("-Rs", "remove", new[] { "--recursive" })]
    [InlineData("-Rn", "remove", new[] { "--nosave" })]
    [InlineData("-Rsn", "remove", new[] { "--recursive", "--nosave" })]
    [InlineData("-Rsc", "remove", new[] { "--recursive", "--cascade" })]
    [InlineData("-Fl", "files", new string[0])]
    [InlineData("-Fo", "owns", new string[0])]
    public void Expand_recognises_pacman_clusters(string token, string expectedSub, string[] expectedFlags)
    {
        var expansion = PacmanFlags.TryExpand(token);
        Assert.NotNull(expansion);
        Assert.Equal(expectedSub, expansion!.Subcommand);
        Assert.Equal(expectedFlags, expansion.InjectedFlags);
    }

    [Theory]
    [InlineData("install")]   // not a cluster
    [InlineData("-aur")]      // all lowercase → not a pacman cluster
    [InlineData("--help")]    // GNU long flag
    [InlineData("-")]         // bare dash
    [InlineData("")]          // empty
    [InlineData("foo")]       // bareword
    public void TryExpand_returns_null_for_non_clusters(string token)
    {
        Assert.Null(PacmanFlags.TryExpand(token));
    }

    [Theory]
    [InlineData("-Sx")]                                                    // unknown -S modifier
    [InlineData("-Qx")]                                                    // unknown -Q modifier
    [InlineData("-Fx")]                                                    // unknown -F modifier
    [InlineData("-Rx")]                                                    // unknown -R modifier
    [InlineData("-Ux")]                                                    // unknown -U modifier
    [InlineData("-Ssw")]                                                   // download-only cannot combine with search
    [InlineData("-F")]                                                     // -F requires a modifier
    public void TryExpand_throws_on_invalid_clusters(string token)
    {
        Assert.Throws<ArgumentException>(() => PacmanFlags.TryExpand(token));
    }

    [Fact]
    public void TryExpand_treats_non_letter_clusters_as_non_pacman()
    {
        Assert.Null(PacmanFlags.TryExpand("-1"));
        Assert.Null(PacmanFlags.TryExpand("-?"));
    }
}
