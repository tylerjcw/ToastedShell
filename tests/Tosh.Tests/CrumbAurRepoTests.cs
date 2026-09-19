using Tosh.Crumb.Aur;
using Tosh.Crumb.Models;

namespace Tosh.Tests;

/// <summary>
/// Which AUR repo holds a package's PKGBUILD.
/// </summary>
/// <remarks>
/// <para>
/// A split PKGBUILD produces many packages out of one repo, and the repo is named after the
/// <em>base</em>. Cloning by package name asks the AUR for a repo that does not exist — and
/// the AUR answers with an <em>empty</em> repository rather than a failure, so
/// <c>git clone</c> exits zero and there is simply no PKGBUILD in the checkout.
/// </para>
/// <para>
/// Found installing <c>dotnet-sdk-preview-bin</c>, which comes out of
/// <c>dotnet-core-preview-bin</c> along with the runtime and targeting packs it depends on.
/// Every one of those clones came back empty.
/// </para>
/// </remarks>
public sealed class CrumbAurRepoTests
{
    private static Package Aur(string name, string? packageBase) =>
        new() { Name = name, Version = "1", Repo = "aur", Base = packageBase };

    /// <summary>The real set that exposed this, names and bases as the AUR reports them.</summary>
    private static readonly Package[] DotNet =
    [
        Aur("dotnet-sdk-preview-bin", "dotnet-core-preview-bin"),
        Aur("dotnet-runtime-preview-bin", "dotnet-core-preview-bin"),
        Aur("dotnet-sdk-bin", "dotnet-core-bin"),
        Aur("dotnet-targeting-pack-bin", "dotnet-core-bin"),
        Aur("aspnet-runtime-bin", "dotnet-core-bin"),
    ];

    [Theory]
    [InlineData("dotnet-sdk-preview-bin", "dotnet-core-preview-bin")]
    [InlineData("dotnet-runtime-preview-bin", "dotnet-core-preview-bin")]
    [InlineData("dotnet-sdk-bin", "dotnet-core-bin")]
    [InlineData("dotnet-targeting-pack-bin", "dotnet-core-bin")]
    [InlineData("aspnet-runtime-bin", "dotnet-core-bin")]
    public void A_split_package_is_cloned_from_its_base(string pkg, string expected)
        => Assert.Equal(expected, AurBuilder.RepoFor(pkg, DotNet));

    /// <summary>
    /// Where the base is the name, which is most packages, nothing changes.
    /// </summary>
    [Fact]
    public void A_package_that_is_its_own_base_clones_its_own_name()
        => Assert.Equal("paru", AurBuilder.RepoFor("paru", [Aur("paru", "paru")]));

    /// <summary>
    /// With nothing known about the package, the name is the best guess available.
    /// </summary>
    /// <remarks>
    /// This is the offline path. Name and base agree unless the PKGBUILD is a split one, so
    /// an unreachable AUR degrades to what this did before rather than to nothing at all.
    /// </remarks>
    [Fact]
    public void With_no_information_the_name_is_used()
    {
        Assert.Equal("something", AurBuilder.RepoFor("something", []));
        Assert.Equal("something", AurBuilder.RepoFor("something", [Aur("unrelated", "other-base")]));
    }

    /// <summary>A base the AUR left empty is not a base.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void An_absent_base_falls_back_to_the_name(string? packageBase)
        => Assert.Equal("thing", AurBuilder.RepoFor("thing", [Aur("thing", packageBase)]));

    /// <summary>
    /// Only the package asked about decides, not whichever came back first.
    /// </summary>
    /// <remarks>
    /// One <c>/info</c> call carries every package in the batch, so the results are a set to
    /// search rather than an answer to read off the front.
    /// </remarks>
    [Fact]
    public void The_package_asked_about_is_the_one_that_decides()
        => Assert.Equal("dotnet-core-bin", AurBuilder.RepoFor("dotnet-sdk-bin", DotNet));

    /// <summary>Names are matched exactly, so a prefix is not a match.</summary>
    [Fact]
    public void A_name_that_merely_starts_the_same_is_not_a_match()
        => Assert.Equal(
            "dotnet-sdk",
            AurBuilder.RepoFor("dotnet-sdk", [Aur("dotnet-sdk-bin", "dotnet-core-bin")]));
}
