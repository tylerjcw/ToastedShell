using System.Text.Json.Serialization;

namespace Tosh.Crumb.Models;

/// <summary>
/// Unified package record. Same shape from pacman sync DBs, the local
/// installed DB, and the AUR — so pipelines downstream do not need to
/// know where the package came from.
/// </summary>
public sealed record Package
{
    public required string Name { get; init; }
    public required string Version { get; init; }

    /// <summary>"core", "extra", "multilib", "aur", or a custom repo.</summary>
    public required string Repo { get; init; }

    public string? Description { get; init; }
    public string? Url { get; init; }
    public string? Maintainer { get; init; }
    public string? Packager { get; init; }
    public string? Architecture { get; init; }
    public string? License { get; init; }
    public string? Base { get; init; }

    public long? DownloadSize { get; init; }
    public long? InstalledSize { get; init; }

    public IReadOnlyList<string> Groups { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Depends { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> MakeDepends { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> CheckDepends { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> OptDepends { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Provides { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Conflicts { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Replaces { get; init; } = Array.Empty<string>();

    public DateTimeOffset? BuildDate { get; init; }
    public DateTimeOffset? InstallDate { get; init; }
    public DateTimeOffset? FirstSubmitted { get; init; }
    public DateTimeOffset? LastModified { get; init; }

    /// <summary>AUR-only: vote count. Null for repo packages.</summary>
    public int? Votes { get; init; }

    /// <summary>AUR-only: popularity score.</summary>
    public double? Popularity { get; init; }

    /// <summary>AUR-only: out-of-date flag timestamp; null if not flagged.</summary>
    public DateTimeOffset? OutOfDate { get; init; }

    /// <summary>True if the package is currently installed (from local DB).</summary>
    public bool Installed { get; init; }

    /// <summary>Locally installed version, if any (joined from local DB).</summary>
    public string? InstalledVersion { get; init; }

    /// <summary>"explicit" or "depend" — only meaningful when Installed=true.</summary>
    public string? InstallReason { get; init; }

    [JsonIgnore] public bool IsAur => string.Equals(Repo, "aur", StringComparison.OrdinalIgnoreCase);
}
