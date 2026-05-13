namespace Tosh.Crumb.Aur;

/// <summary>
/// VCS package detection. Mirrors the suffixes pacman/makepkg treat as
/// "always rebuild against upstream": the AUR <c>Version</c> for these
/// is a static fallback baked into <c>.SRCINFO</c>, not the live value
/// <c>pkgver()</c> produces during build, so version comparison
/// against AUR metadata is meaningless for them.
/// </summary>
internal static class Vcs
{
    private static readonly string[] Suffixes =
    {
        "-git", "-svn", "-hg", "-bzr", "-cvs", "-darcs",
    };

    public static bool IsVcs(string name)
    {
        foreach (var s in Suffixes)
            if (name.EndsWith(s, StringComparison.Ordinal)) return true;
        return false;
    }
}
