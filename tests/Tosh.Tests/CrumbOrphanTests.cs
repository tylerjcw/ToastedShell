using Tosh.Crumb.Commands;
using Tosh.Crumb.Pacman;

namespace Tosh.Tests;

/// <summary>
/// Which installed packages are still wanted, and therefore which are orphans.
/// </summary>
/// <remarks>
/// <para>
/// <c>crumb -Qt</c> reported 80 orphans where <c>pacman -Qdt</c> reported 43 on the
/// author's machine — 37 packages it would have told someone they could safely delete.
/// Two rules were missing, and each is covered below with a package that only that
/// rule keeps alive.
/// </para>
/// <para>
/// The fixture is a real pacman root on disk rather than a stub, because the parser and
/// the orphan rule fail together: a <c>%OPTDEPENDS%</c> entry that is read but not
/// parsed looks exactly like one that is never read.
/// </para>
/// </remarks>
public sealed class CrumbOrphanTests : IDisposable
{
    private readonly string _root =
        Directory.CreateTempSubdirectory("crumb-orphan-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    /// <param name="reason">"1" marks a package installed as a dependency, as pacman does.</param>
    private void Install(
        string name,
        string? reason = null,
        string[]? depends = null,
        string[]? optdepends = null,
        string[]? provides = null)
    {
        var dir = Path.Combine(_root, "local", $"{name}-1.0-1");
        Directory.CreateDirectory(dir);

        var desc = new List<string> { "%NAME%", name, "", "%VERSION%", "1.0-1", "" };
        if (reason is not null) { desc.Add("%REASON%"); desc.Add(reason); desc.Add(""); }
        void Section(string key, string[]? values)
        {
            if (values is null || values.Length == 0) return;
            desc.Add($"%{key}%");
            desc.AddRange(values);
            desc.Add("");
        }
        Section("DEPENDS", depends);
        Section("OPTDEPENDS", optdepends);
        Section("PROVIDES", provides);

        File.WriteAllLines(Path.Combine(dir, "desc"), desc);
    }

    private string[] Orphans()
    {
        var db = new PacmanDb(_root);
        var needed = CrumbCommands.NeededPackages(db);
        return db.Local.Values
            .Where(p => p.InstallReason == "depend" && !needed.Contains(p.Name))
            .Select(p => p.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// A dependency satisfied under another name. `lutris` wants `p7zip`; `7zip`
    /// provides it, and is not an orphan for that reason alone.
    /// </summary>
    [Fact]
    public void A_package_providing_what_something_depends_on_is_not_an_orphan()
    {
        Install("lutris", depends: ["p7zip"]);
        Install("7zip", reason: "1", provides: ["p7zip"]);
        Install("unused", reason: "1");

        Assert.Equal(["unused"], Orphans());
    }

    /// <summary>
    /// An optional dependency keeps a package too — and pacman writes those as
    /// <c>name: description</c>, so the name has to be cut out of the line before it
    /// matches anything.
    /// </summary>
    [Fact]
    public void A_package_listed_as_an_optional_dependency_is_not_an_orphan()
    {
        Install("lutris", optdepends: ["gamemode: Allows games to request optimisations"]);
        Install("gamemode", reason: "1");
        Install("unused", reason: "1");

        Assert.Equal(["unused"], Orphans());
    }

    /// <summary>
    /// The description may itself contain a version, which is why it is cut before the
    /// version constraint rather than after — cutting the other way leaves
    /// <c>"foo: needs bar"</c> intact past the colon.
    /// </summary>
    [Fact]
    public void An_optional_dependency_description_containing_a_version_still_resolves()
    {
        Install("host", optdepends: ["helper: required for foo>=1.2 support"]);
        Install("helper", reason: "1");

        Assert.Empty(Orphans());
    }

    /// <summary>A version constraint on an ordinary dependency is still stripped.</summary>
    [Fact]
    public void A_versioned_dependency_matches_the_package_that_satisfies_it()
    {
        Install("host", depends: ["helper>=1.2"]);
        Install("helper", reason: "1");

        Assert.Empty(Orphans());
    }

    /// <summary>
    /// The control that gives the rest their meaning: a package nothing refers to, by
    /// any of these routes, is still reported.
    /// </summary>
    [Fact]
    public void A_package_nothing_refers_to_is_an_orphan()
    {
        Install("host", depends: ["helper"], optdepends: ["extra: nice to have"]);
        Install("helper", reason: "1");
        Install("extra", reason: "1");
        Install("forgotten", reason: "1");

        Assert.Equal(["forgotten"], Orphans());
    }

    /// <summary>
    /// And an explicitly installed package is never an orphan, however unreferenced —
    /// the user asked for it.
    /// </summary>
    [Fact]
    public void An_explicitly_installed_package_is_never_an_orphan()
    {
        Install("chosen");
        Install("forgotten", reason: "1");

        Assert.Equal(["forgotten"], Orphans());
    }
}
