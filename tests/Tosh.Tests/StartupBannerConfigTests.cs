using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// The greeting, and where a reader's own library lives.
/// </summary>
/// <remarks>
/// The banner was three lines of hard-coded text. Making it a setting is mostly about the
/// second line — a reader who has read "Type 'help browse'" a thousand times should be able
/// to stop being told, and one who wants their own greeting should not have to rebuild the
/// shell to get it.
/// </remarks>
public sealed class StartupBannerConfigTests : IClassFixture<ToshRuntimeFixture>
{
    private readonly ToshRuntime _runtime;

    public StartupBannerConfigTests(ToshRuntimeFixture fixture) => _runtime = fixture.Runtime;

    private async Task<string> Value(string source)
    {
        var results = await new ToshEngine(_runtime.Language).ExecuteToListAsync(source);

        return results.Select(r => r?.ToString()).Last() ?? string.Empty;
    }

    private static ToshStartupConfig Fresh() => new(Path.Combine(Path.GetTempPath(), "tosh-startup-tests"));

    /// <summary>
    /// The library sits beside the other startup locations, under the same root.
    /// </summary>
    /// <remarks>
    /// Which is where a reader who already keeps one has put it. Nothing loads from here at
    /// startup: this says where a library <em>is</em>, so code can later be asked for by
    /// name. A dotted name means nothing until there is a directory for it to be relative to.
    /// </remarks>
    [Fact]
    public void The_library_directory_defaults_beside_the_other_startup_paths()
    {
        var startup = Fresh();

        Assert.Equal(Path.Combine(startup.RootDirectory, "lib"), startup.LibraryDirectory);
    }

    /// <summary>Moving the root moves the library with it.</summary>
    [Fact]
    public void The_library_directory_follows_the_root()
    {
        var startup = Fresh();
        var moved = Path.Combine(Path.GetTempPath(), "tosh-startup-tests-moved");

        startup.ApplyRootDirectory(moved);

        Assert.Equal(Path.Combine(Path.GetFullPath(moved), "lib"), startup.LibraryDirectory);
    }

    /// <summary>An explicit directory is kept, and an empty one falls back.</summary>
    [Fact]
    public void An_explicit_library_directory_is_kept()
    {
        var startup = Fresh();

        startup.LibraryDirectory = "/opt/tosh-library";
        Assert.Equal("/opt/tosh-library", startup.LibraryDirectory);

        startup.LibraryDirectory = "   ";
        Assert.Equal(Path.Combine(startup.RootDirectory, "lib"), startup.LibraryDirectory);
    }

    /// <summary>The banner is shown by default, and can be turned off.</summary>
    [Fact]
    public void The_banner_is_on_by_default_and_can_be_turned_off()
    {
        var startup = Fresh();

        Assert.True(startup.DisplayBanner);

        startup.DisplayBanner = false;
        Assert.False(startup.DisplayBanner);
    }

    /// <summary>Reset puts the greeting back, not just the paths.</summary>
    /// <remarks>
    /// Reset used to restore only what the root derives. A reader who turned the banner off
    /// and then reset the config would have found it still off, which is the kind of gap
    /// that makes "reset" untrustworthy.
    /// </remarks>
    [Fact]
    public void Reset_restores_the_banner_as_well_as_the_paths()
    {
        var startup = Fresh();

        startup.DisplayBanner = false;
        startup.BannerContents = "something else";
        startup.LibraryDirectory = "/opt/elsewhere";

        startup.Reset();

        Assert.True(startup.DisplayBanner);
        Assert.Equal(ToshStartupConfig.DefaultBannerContents, startup.BannerContents);
        Assert.Equal(Path.Combine(startup.RootDirectory, "lib"), startup.LibraryDirectory);
    }

    /// <summary>Null contents read back as empty rather than throwing later.</summary>
    /// <remarks>
    /// Empty means the same as the banner being off: there is no blank greeting worth
    /// printing, and the printer treats the two the same way.
    /// </remarks>
    [Fact]
    public void Empty_contents_are_allowed()
    {
        var startup = Fresh();

        startup.BannerContents = string.Empty;

        Assert.Equal(string.Empty, startup.BannerContents);
    }

    /// <summary>All three are reachable and settable from a config file.</summary>
    /// <remarks>
    /// Which is the whole point — `config.tosh` is ordinary TōSh, so these are ordinary
    /// assignments.
    /// </remarks>
    [Fact]
    public async Task The_settings_are_reachable_from_script()
    {
        var text = await Value("""
            $tosh.Config.Startup.DisplayBanner = false
            $tosh.Config.Startup.BannerContents = "hello"
            $tosh.Config.Startup.LibraryDirectory = "/opt/lib"
            echo $"{$tosh.Config.Startup.DisplayBanner}|{$tosh.Config.Startup.BannerContents}|{$tosh.Config.Startup.LibraryDirectory}"
            """);

        Assert.Equal("false|hello|/opt/lib", text);
    }
}
