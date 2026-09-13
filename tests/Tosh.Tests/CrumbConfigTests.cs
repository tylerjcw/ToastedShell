using Tosh.Crumb.Config;

namespace Tosh.Tests;

/// <summary>
/// Crumb's configuration file, which is ToastScript.
/// </summary>
/// <remarks>
/// <para>
/// The file's value is a record. Using the language the user already has, rather than a
/// format invented for the purpose, is the point: it needs no parser of Crumb's own, it
/// is the syntax they write everywhere else, and a setting can be computed — which the
/// third test here holds to.
/// </para>
/// <para>
/// Every test asserts a *default* survives when the file cannot supply one, because the
/// failure that matters is a package manager refusing to run over a typo in a
/// preference file.
/// </para>
/// </remarks>
public sealed class CrumbConfigTests : IDisposable
{
    private readonly string _root =
        Directory.CreateTempSubdirectory("crumb-config-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Write(string source)
    {
        var path = Path.Combine(_root, "crumb.tosh");
        File.WriteAllText(path, source);
        return path;
    }

    [Fact]
    public void A_missing_file_gives_defaults()
    {
        var cfg = CrumbConfig.Load(Path.Combine(_root, "absent.tosh"));

        Assert.Null(cfg.Pager);
        Assert.Null(cfg.Quiet);
        Assert.Empty(cfg.Exclude);
        Assert.Empty(cfg.MakepkgFlags);
    }

    [Fact]
    public void The_records_fields_become_settings()
    {
        var cfg = CrumbConfig.Load(Write("""
            {|
                quiet        = true
                verbose      = false
                pager        = "bat"
                truecolor    = false
                review       = true
                exclude      = ["linux", "nvidia"]
                makepkgFlags = ["--skippgpcheck"]
            |}
            """));

        Assert.True(cfg.Quiet);
        Assert.False(cfg.Verbose);
        Assert.Equal("bat", cfg.Pager);
        Assert.False(cfg.Truecolor);
        Assert.True(cfg.Review);
        Assert.Equal(["linux", "nvidia"], cfg.Exclude);
        Assert.Equal(["--skippgpcheck"], cfg.MakepkgFlags);
    }

    /// <summary>
    /// The reason this is ToastScript and not a table format: a setting can be worked
    /// out rather than written down.
    /// </summary>
    [Fact]
    public void A_setting_can_be_computed()
    {
        var cfg = CrumbConfig.Load("""
            var host = "valinor"
            var extra = ["nvidia"]

            {|
                pager   = ($host == "valinor" ? "bat" : "less")
                exclude = [...$extra, $"{$host}-kernel"]
            |}
            """ is var source ? Write(source) : throw new InvalidOperationException());

        Assert.Equal("bat", cfg.Pager);
        Assert.Equal(["nvidia", "valinor-kernel"], cfg.Exclude);
    }

    /// <summary>
    /// A record written `Pager` means the same as `pager`, and failing silently over a
    /// capital letter is a poor way to learn the difference.
    /// </summary>
    [Fact]
    public void Field_names_are_matched_without_regard_to_case()
    {
        var cfg = CrumbConfig.Load(Write("{| Pager = \"bat\", Quiet = true |}"));

        Assert.Equal("bat", cfg.Pager);
        Assert.True(cfg.Quiet);
    }

    /// <summary>One value where a list is expected reads the way someone means it.</summary>
    [Fact]
    public void A_single_value_is_accepted_where_a_list_is_expected()
    {
        var cfg = CrumbConfig.Load(Write("{| exclude = \"linux\" |}"));

        Assert.Equal(["linux"], cfg.Exclude);
    }

    /// <summary>
    /// A file that does not parse, or does not end in a record, is a warning and the
    /// defaults — never a refusal to run.
    /// </summary>
    [Theory]
    [InlineData("{| this is not valid ToastScript")]
    [InlineData("var x = 1")]
    [InlineData("")]
    public void A_file_that_cannot_be_read_leaves_the_defaults(string source)
    {
        var cfg = CrumbConfig.Load(Write(source));

        Assert.Null(cfg.Pager);
        Assert.Null(cfg.Quiet);
        Assert.Empty(cfg.Exclude);
    }

    /// <summary>An unknown field is someone else's, or a later version's.</summary>
    [Fact]
    public void An_unknown_field_is_ignored()
    {
        var cfg = CrumbConfig.Load(Write("{| pager = \"bat\", somethingElse = 42 |}"));

        Assert.Equal("bat", cfg.Pager);
    }

    /// <summary>
    /// `CRUMB_PAGER` is a choice about this run and outranks the file, which is a choice
    /// about Crumb, which outranks `PAGER`, which is a choice about the system.
    /// </summary>
    [Fact]
    public void An_explicit_pager_outranks_the_environment()
    {
        var previous = Environment.GetEnvironmentVariable("CRUMB_PAGER");
        try
        {
            Environment.SetEnvironmentVariable("CRUMB_PAGER", "from-env");
            Assert.Equal("from-flag", CrumbConfig.ResolvePager("from-flag"));
            Assert.Equal("from-env", CrumbConfig.ResolvePager(null));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CRUMB_PAGER", previous);
        }
    }
}
