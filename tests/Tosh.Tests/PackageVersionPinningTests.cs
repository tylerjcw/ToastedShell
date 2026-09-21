using System.Text.RegularExpressions;

namespace Tosh.Tests;

/// <summary>
/// Every `PackageReference` names an exact version — <c>TOSH-0002</c>.
/// </summary>
/// <remarks>
/// <para>
/// A floating reference means two restores months apart can build differently from the same
/// commit, and nothing records which one was tested. `Tosh.DevCompanion` carried
/// <c>Version="9.*"</c> for long enough that an advisory on a transitive package appeared and
/// resolved itself without anybody acting — which is the finding, because it could equally
/// have appeared and stayed.
/// </para>
/// <para>
/// This is a repository-shape test rather than a behaviour test. It exists because the float
/// was found by following a security note, not by anything that looks for floats, so the next
/// one would have arrived the same way: unremarked, in a project nothing ships and nobody
/// re-reads.
/// </para>
/// </remarks>
public sealed class PackageVersionPinningTests
{
    private static readonly string RepositoryRoot =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));

    private static readonly Regex Reference = new(
        """<PackageReference\b(?<attributes>[^>]*?)/?>""",
        RegexOptions.Singleline);

    private static readonly Regex Attribute = new(@"(?<name>\w+)\s*=\s*""(?<value>[^""]*)""");

    /// <summary>Project files, excluding build output and anything not checked in.</summary>
    private static IEnumerable<string> Projects() =>
        Directory.EnumerateFiles(RepositoryRoot, "*.csproj", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                                          StringComparison.Ordinal)
                        && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                                          StringComparison.Ordinal));

    [Fact]
    public void Every_package_reference_names_an_exact_version()
    {
        var offenders = new List<string>();

        foreach (var project in Projects())
        {
            var relative = Path.GetRelativePath(RepositoryRoot, project);

            foreach (Match reference in Reference.Matches(File.ReadAllText(project)))
            {
                var attributes = Attribute.Matches(reference.Groups["attributes"].Value)
                    .ToDictionary(
                        match => match.Groups["name"].Value,
                        match => match.Groups["value"].Value,
                        StringComparer.OrdinalIgnoreCase);

                if (!attributes.TryGetValue("Include", out var package))
                {
                    continue;
                }

                if (!attributes.TryGetValue("Version", out var version) || version.Length == 0)
                {
                    offenders.Add($"{relative}: '{package}' has no Version");
                    continue;
                }

                // A wildcard floats. So does interval notation — `[9.0,10.0)` pins no more
                // than `9.*` does, it only spells the range out.
                if (version.Contains('*', StringComparison.Ordinal) ||
                    version.StartsWith('[') || version.StartsWith('('))
                {
                    offenders.Add($"{relative}: '{package}' floats at Version=\"{version}\"");
                }
            }
        }

        Assert.Empty(offenders);
    }

    /// <summary>
    /// The test can actually see the projects it claims to check.
    /// </summary>
    /// <remarks>
    /// A path walk that finds nothing passes an "everything is fine" assertion silently, which
    /// is the failure mode this kind of test is prone to. The count is a floor, not the exact
    /// number, so adding a project does not break it.
    /// </remarks>
    [Fact]
    public void The_audit_reaches_the_projects_it_audits()
    {
        var projects = Projects().ToList();

        Assert.True(projects.Count >= 10, $"found only {projects.Count} project files under {RepositoryRoot}");
        Assert.Contains(projects, path => path.EndsWith("Tosh.DevCompanion.csproj", StringComparison.Ordinal));
    }
}
