namespace Tosh.Crumb.Commands;

/// <summary>
/// Expands pacman-style stacked short flags (e.g. <c>-Ss</c>, <c>-Qqe</c>,
/// <c>-SsJ</c>) into the equivalent subcommand + long-form options so the
/// rest of the parser can stay subcommand-driven.
///
/// Rules:
///   The first arg, if it starts with a single dash followed by at least
///   one ASCII letter and contains at least one uppercase letter in that
///   cluster, is treated as a pacman-style "operation+modifiers" token.
///   The cluster is consumed in order:
///     • The first uppercase letter selects the operation (-S, -Q, -F).
///     • Subsequent lowercase letters add modifiers in any order.
///     • Subsequent uppercase letters add output modifiers (-J json,
///       -N ndjson, -T tsv).
/// </summary>
public static class PacmanFlags
{
    public sealed record Expansion(string Subcommand, IReadOnlyList<string> InjectedFlags);

    /// <summary>Returns null if <paramref name="token"/> isn't a pacman-style cluster.</summary>
    public static Expansion? TryExpand(string token)
    {
        if (token.Length < 2 || token[0] != '-' || token[1] == '-') return null;
        var cluster = token.AsSpan(1);
        var hasUpper = false;
        foreach (var c in cluster)
        {
            if (!char.IsAsciiLetter(c)) return null;
            if (char.IsUpper(c)) hasUpper = true;
        }
        if (!hasUpper) return null;

        var op = '\0';
        var mods = new List<char>(cluster.Length);
        foreach (var c in cluster)
        {
            if (char.IsUpper(c) && op == '\0' && c is 'S' or 'Q' or 'F' or 'R' or 'U')
                op = c;
            else
                mods.Add(c);
        }
        if (op == '\0') return null; // No operation letter — not a pacman cluster; let the regular parser handle it.

        return op switch
        {
            'S' => ExpandSync(mods),
            'Q' => ExpandQuery(mods),
            'F' => ExpandFiles(mods),
            'R' => ExpandRemove(mods),
            'U' => throw new ArgumentException("-U (install from file) is not implemented yet"),
            _ => throw new ArgumentException($"unknown operation -{op}"),
        };
    }

    private static Expansion ExpandSync(List<char> mods)
    {
        // -S* operations target the sync repos (and AUR by default in crumb).
        // With no read-modifier (s/i/l) and no positional context, -S means
        // "install"; -Sy refresh, -Su upgrade, -Syu update.
        string? sub = null;
        var flags = new List<string>();
        foreach (var m in mods)
        {
            switch (m)
            {
                case 's': sub ??= "search"; break;
                case 'i': sub ??= "info"; break;
                case 'l': sub ??= "list"; break; // -Sl: list packages in repo (TODO)
                case 'a': flags.Add("--aur-only"); break;
                case 'r': flags.Add("--repos-only"); break;
                case 'q': flags.Add("--names"); break;
                case 'v': flags.Add("--verbose"); break;
                case 'J': flags.Add("--json"); break;
                case 'N': flags.Add("--ndjson"); break;
                case 'T': flags.Add("--tsv"); break;
                case 'y': flags.Add("--refresh"); break;
                case 'u': flags.Add("--upgrade"); break;
                case 'w':
                    throw new ArgumentException("-Sw (download only) is not implemented yet");
                default:
                    throw new ArgumentException($"unknown -S modifier '-{m}'");
            }
        }
        // If no read-modifier was chosen, this is an install / sync / upgrade.
        if (sub is null)
        {
            var refresh = flags.Contains("--refresh");
            var upgrade = flags.Contains("--upgrade");
            if (refresh && !upgrade) sub = "sync";       // -Sy
            else if (upgrade) sub = "update";     // -Su, -Syu
            else sub = "install";    // bare -S
        }
        return new Expansion(sub, flags);
    }

    private static Expansion ExpandQuery(List<char> mods)
    {
        // -Q* operations target the local installed DB.
        string sub = "list"; // bare -Q ⇒ list installed
        var flags = new List<string>();
        foreach (var m in mods)
        {
            switch (m)
            {
                case 's': sub = "list"; break;          // -Qs <term>: filter installed by name substring
                case 'i': sub = "info"; flags.Add("--installed"); break;
                case 'l': sub = "files"; break;         // -Ql <pkg>: files owned by pkg
                case 'o': sub = "owns"; break;          // -Qo <path>
                case 'e': sub = "list"; flags.Add("--explicit"); break;
                case 't': sub = "list"; flags.Add("--orphans"); break;
                case 'm': sub = "list"; flags.Add("--foreign"); break;
                case 'q': flags.Add("--names"); break;
                case 'v': flags.Add("--verbose"); break;
                case 'J': flags.Add("--json"); break;
                case 'N': flags.Add("--ndjson"); break;
                case 'T': flags.Add("--tsv"); break;
                case 'd':
                    throw new ArgumentException("-Qd (dep-installed filter) is not implemented yet");
                default:
                    throw new ArgumentException($"unknown -Q modifier '-{m}'");
            }
        }
        return new Expansion(sub, flags);
    }

    private static Expansion ExpandRemove(List<char> mods)
    {
        // -R* operations remove installed packages.
        var flags = new List<string>();
        foreach (var m in mods)
        {
            switch (m)
            {
                case 's': flags.Add("--recursive"); break; // remove deps too
                case 'c': flags.Add("--cascade"); break;
                case 'n': flags.Add("--nosave"); break;    // do not save .pacsave backups
                case 'd':
                    throw new ArgumentException("-Rd (skip dependency checks) is not implemented yet");
                default:
                    throw new ArgumentException($"unknown -R modifier '-{m}'");
            }
        }
        return new Expansion("remove", flags);
    }

    private static Expansion ExpandFiles(List<char> mods)
    {
        // -F* operations consult the sync file index. v0.1 maps these onto the
        // installed-DB equivalents where possible.
        string? sub = null;
        var flags = new List<string>();
        foreach (var m in mods)
        {
            switch (m)
            {
                case 'l': sub = "files"; break;
                case 'o': sub = "owns"; break;
                case 'q': flags.Add("--names"); break;
                case 'v': flags.Add("--verbose"); break;
                case 'J': flags.Add("--json"); break;
                case 'N': flags.Add("--ndjson"); break;
                case 'T': flags.Add("--tsv"); break;
                case 'y':
                    throw new ArgumentException("-Fy (file db refresh) is not implemented yet");
                default:
                    throw new ArgumentException($"unknown -F modifier '-{m}'");
            }
        }
        if (sub is null) throw new ArgumentException("-F requires a modifier (-Fl files, -Fo owns, …)");
        return new Expansion(sub, flags);
    }
}
