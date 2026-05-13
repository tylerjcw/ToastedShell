using Tosh.Crumb.Commands;
using Tosh.Crumb.Output;

namespace Tosh.Tests;

public class CrumbOptionsTests
{
    [Theory]
    [InlineData("--group-by", "repo")]
    [InlineData("--group-by", "source")]
    [InlineData("--group-by", "version")]
    public void GroupBy_parses_value(string flag, string value)
    {
        var o = CrumbOptions.Parse(new[] { flag, value });
        Assert.Equal(value, o.GroupBy);
    }
    [Fact]
    public void Positional_collected_in_order()
    {
        var o = CrumbOptions.Parse(new[] { "foo", "bar", "baz" });
        Assert.Equal(new[] { "foo", "bar", "baz" }, o.Positional);
    }

    [Fact]
    public void DoubleDash_stops_flag_parsing()
    {
        var o = CrumbOptions.Parse(new[] { "--", "--aur-only", "-Ss" });
        Assert.False(o.AurOnly);
        Assert.Equal(new[] { "--aur-only", "-Ss" }, o.Positional);
    }

    [Theory]
    [InlineData("--aur-only")]
    [InlineData("--aur")]
    public void AurOnly_aliases(string flag)
    {
        Assert.True(CrumbOptions.Parse(new[] { flag }).AurOnly);
    }

    [Theory]
    [InlineData("--repos-only")]
    [InlineData("--repos")]
    [InlineData("--repo")]
    public void ReposOnly_aliases(string flag)
    {
        Assert.True(CrumbOptions.Parse(new[] { flag }).ReposOnly);
    }

    [Theory]
    [InlineData("--json", OutputFormat.Json)]
    [InlineData("--ndjson", OutputFormat.Ndjson)]
    [InlineData("--tsv", OutputFormat.Tsv)]
    [InlineData("--names", OutputFormat.Names)]
    [InlineData("-J", OutputFormat.Json)]
    [InlineData("-N", OutputFormat.Ndjson)]
    [InlineData("-T", OutputFormat.Tsv)]
    [InlineData("-q", OutputFormat.Names)]
    public void Format_shortcuts(string flag, OutputFormat expected)
    {
        Assert.Equal(expected, CrumbOptions.Parse(new[] { flag }).Format);
    }

    [Theory]
    [InlineData("auto", OutputFormat.Auto)]
    [InlineData("table", OutputFormat.Table)]
    [InlineData("json", OutputFormat.Json)]
    [InlineData("ndjson", OutputFormat.Ndjson)]
    [InlineData("jsonl", OutputFormat.Ndjson)]
    [InlineData("tsv", OutputFormat.Tsv)]
    [InlineData("names", OutputFormat.Names)]
    [InlineData("name", OutputFormat.Names)]
    [InlineData("tssp", OutputFormat.Tssp)]
    public void Format_values_via_equals(string value, OutputFormat expected)
    {
        Assert.Equal(expected, CrumbOptions.Parse(new[] { $"--format={value}" }).Format);
    }

    [Fact]
    public void Format_values_via_space()
    {
        Assert.Equal(OutputFormat.Json, CrumbOptions.Parse(new[] { "--format", "json" }).Format);
        Assert.Equal(OutputFormat.Json, CrumbOptions.Parse(new[] { "-f", "json" }).Format);
    }

    [Fact]
    public void Format_unknown_throws()
    {
        Assert.Throws<ArgumentException>(() => CrumbOptions.Parse(new[] { "--format=bogus" }));
    }

    [Fact]
    public void Format_missing_value_throws()
    {
        Assert.Throws<ArgumentException>(() => CrumbOptions.Parse(new[] { "--format" }));
    }

    [Fact]
    public void Limit_space_form()
    {
        Assert.Equal(10, CrumbOptions.Parse(new[] { "--limit", "10" }).Limit);
    }

    [Fact]
    public void Limit_equals_form()
    {
        Assert.Equal(25, CrumbOptions.Parse(new[] { "--limit=25" }).Limit);
    }

    [Fact]
    public void Limit_zero_allowed()
    {
        Assert.Equal(0, CrumbOptions.Parse(new[] { "--limit=0" }).Limit);
    }

    [Fact]
    public void Limit_missing_value_throws()
    {
        Assert.Throws<ArgumentException>(() => CrumbOptions.Parse(new[] { "--limit" }));
    }

    [Fact]
    public void Limit_negative_throws()
    {
        Assert.Throws<ArgumentException>(() => CrumbOptions.Parse(new[] { "--limit=-3" }));
    }

    [Fact]
    public void Limit_non_numeric_throws()
    {
        Assert.Throws<ArgumentException>(() => CrumbOptions.Parse(new[] { "--limit=abc" }));
    }

    [Fact]
    public void Needed_NoConfirm_AsDeps_flags()
    {
        var o = CrumbOptions.Parse(new[] { "--needed", "--noconfirm", "--asdeps" });
        Assert.True(o.Needed);
        Assert.True(o.NoConfirm);
        Assert.True(o.AsDeps);
    }

    [Fact]
    public void Unknown_flag_throws()
    {
        Assert.Throws<ArgumentException>(() => CrumbOptions.Parse(new[] { "--no-such-flag" }));
    }

    [Fact]
    public void Lone_dash_is_positional()
    {
        var o = CrumbOptions.Parse(new[] { "-" });
        Assert.Equal(new[] { "-" }, o.Positional);
    }

    [Fact]
    public void DryRun_aliases()
    {
        Assert.True(CrumbOptions.Parse(new[] { "--dry-run" }).DryRun);
        Assert.True(CrumbOptions.Parse(new[] { "-n" }).DryRun);
    }

    [Fact]
    public void SearchBy_value()
    {
        Assert.Equal("maintainer", CrumbOptions.Parse(new[] { "--by", "maintainer" }).SearchBy);
        Assert.Equal("depends", CrumbOptions.Parse(new[] { "--by=depends" }).SearchBy);
    }
}
