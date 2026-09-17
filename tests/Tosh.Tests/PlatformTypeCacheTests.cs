using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// The cached platform index finds exactly the record asked for — `TOAST-0064`.
/// </summary>
/// <remarks>
/// <para>
/// The index of every platform type is cached between runs as a **sorted** record file and
/// searched in place rather than parsed into a dictionary: building a 32,000-entry
/// dictionary costs about 60 ms, which is most of what the cache exists to save. What that
/// buys is a binary search, and a binary search that is wrong returns the *wrong type*
/// rather than failing — a `System.IO.File` annotation quietly meaning `System.IO.FileInfo`.
/// </para>
/// <para>
/// These exist because the obvious test does not work. Resolving names through the public
/// surface exercises this only when a cache exists for the running framework's exact
/// trusted-platform set, and `dotnet test` has a different one from a published shell — so
/// the file is never read there. A negative control proved it: skewing the search by one
/// record failed nothing at all.
/// </para>
/// </remarks>
public sealed class PlatformTypeCacheTests
{
    private static readonly string[] Assemblies = ["AssemblyOne", "AssemblyTwo"];

    /// <summary>Builds a cache file the same way the writer does.</summary>
    private static DotNetTypeResolver.PlatformTypeCacheFile Build(params string[] keys)
    {
        var records = keys
            .Select((key, index) => $"{key.ToLowerInvariant()}\t{index % Assemblies.Length}\tType.For.{key}")
            .OrderBy(record => record, StringComparer.Ordinal)
            .ToArray();

        return DotNetTypeResolver.PlatformTypeCacheFile.FromRecords(records, Assemblies);
    }

    /// <summary>Every key present is found, wherever it falls in the ordering.</summary>
    /// <remarks>
    /// Exhaustive over the file rather than sampled: the failures a binary search has are
    /// positional, so the only convincing test is every position.
    /// </remarks>
    [Fact]
    public void Every_key_is_found()
    {
        var keys = Enumerable.Range(0, 200).Select(index => $"System.Namespace{index:D3}.Type{index:D3}").ToArray();
        var cache = Build(keys);

        foreach (var key in keys)
        {
            Assert.True(cache.TryGet(key, out var found), key);
            Assert.Equal($"Type.For.{key}, {Assemblies[Array.IndexOf(keys, key) % Assemblies.Length]}", found);
        }
    }

    /// <summary>The first and last records are found.</summary>
    /// <remarks>The two a search most often walks past.</remarks>
    [Fact]
    public void The_edges_are_found()
    {
        var cache = Build("aaa.first", "mmm.middle", "zzz.last");

        Assert.True(cache.TryGet("aaa.first", out var first));
        Assert.Equal("Type.For.aaa.first, AssemblyOne", first);

        Assert.True(cache.TryGet("zzz.last", out var last));
        Assert.Equal("Type.For.zzz.last, AssemblyOne", last);
    }

    /// <summary>A single-record file works.</summary>
    [Fact]
    public void One_record_is_found()
    {
        var cache = Build("only.one");

        Assert.True(cache.TryGet("only.one", out var found));
        Assert.Equal("Type.For.only.one, AssemblyOne", found);
    }

    /// <summary>
    /// A key that is not there is reported absent, not approximated.
    /// </summary>
    /// <remarks>
    /// The case the whole cache exists for. A miss here is what lets a lookup answer
    /// "not a CLR type" without building the live index, so a miss reported as a *hit* on a
    /// neighbouring record would be the worst outcome available.
    /// </remarks>
    [Theory]
    [InlineData("aaa.before-everything")]
    [InlineData("nnn.between")]
    [InlineData("zzzz.after-everything")]
    [InlineData("mmm.middl")]
    [InlineData("mmm.middlee")]
    public void An_absent_key_is_absent(string missing)
    {
        var cache = Build("bbb.one", "mmm.middle", "yyy.last");

        Assert.False(cache.TryGet(missing, out var found), missing);
        Assert.Null(found);
    }

    /// <summary>Lookup is case-insensitive, as the dictionary it replaced was.</summary>
    [Theory]
    [InlineData("System.IO.File")]
    [InlineData("system.io.file")]
    [InlineData("SYSTEM.IO.FILE")]
    public void Lookup_ignores_case(string query)
    {
        var cache = Build("System.IO.File", "System.IO.FileInfo");

        Assert.True(cache.TryGet(query, out var found), query);

        // The key is lower-cased so an ordinal comparison is the case-insensitive one; the
        // *value* keeps the type's real casing, which is what the loader is handed.
        Assert.Equal("Type.For.System.IO.File, AssemblyOne", found);
    }

    /// <summary>
    /// Adjacent keys sharing a prefix are told apart.
    /// </summary>
    /// <remarks>
    /// `File` against `FileInfo` against `FileStream` is the real shape of this: they sort
    /// next to each other, they are all real, and a search that stops one short returns
    /// one of the others rather than failing.
    /// </remarks>
    [Theory]
    [InlineData("system.io.file", "System.IO.File", "AssemblyOne")]
    [InlineData("system.io.fileinfo", "System.IO.FileInfo", "AssemblyTwo")]
    [InlineData("system.io.filestream", "System.IO.FileStream", "AssemblyOne")]
    public void Keys_sharing_a_prefix_are_told_apart(string query, string expectedType, string expectedAssembly)
    {
        var cache = Build("System.IO.File", "System.IO.FileInfo", "System.IO.FileStream");

        Assert.True(cache.TryGet(query, out var found), query);
        Assert.Equal($"Type.For.{expectedType}, {expectedAssembly}", found);
    }

    /// <summary>An empty file finds nothing rather than throwing.</summary>
    [Fact]
    public void An_empty_cache_finds_nothing()
    {
        var cache = DotNetTypeResolver.PlatformTypeCacheFile.FromRecords([], Assemblies);

        Assert.False(cache.TryGet("anything.at.all", out var found));
        Assert.Null(found);
    }

    /// <summary>A record naming an assembly that is not there is refused, not guessed.</summary>
    [Fact]
    public void A_record_pointing_outside_the_assembly_list_is_refused()
    {
        var cache = DotNetTypeResolver.PlatformTypeCacheFile.FromRecords(
            ["broken.record\t99\tType.For.broken"],
            Assemblies);

        Assert.False(cache.TryGet("broken.record", out var found));
        Assert.Null(found);
    }

    // ── What keys the file, and what bounds the directory (TOAST-0134) ──

    private static (long Length, long Ticks)? Stat(long length, long ticks)
        => (length, ticks);

    /// <summary>
    /// A rebuilt assembly changes the stamp, so it cannot be answered from an old index.
    /// </summary>
    /// <remarks>
    /// This is the whole of <c>TOAST-0134</c>. The fingerprint used to be the assembly
    /// <em>paths</em>, which a rebuild does not change — so a rebuilt binary read an index
    /// built from older source, and because a miss in the cache is answered authoritatively,
    /// every type added since did not exist. It cost seventeen days of
    /// <c>new Tui.TuiGauge()</c> failing for no visible reason.
    /// </remarks>
    [Fact]
    public void A_rebuilt_assembly_changes_the_stamp()
    {
        string[] paths = ["/app/Tosh.Cli.dll", "/app/Tosh.Tui.dll"];

        var before = DotNetTypeResolver.ApplicationStamp(paths, "/app/", _ => Stat(1000, 100));
        var rebuilt = DotNetTypeResolver.ApplicationStamp(paths, "/app/", _ => Stat(1000, 200));
        var resized = DotNetTypeResolver.ApplicationStamp(paths, "/app/", _ => Stat(1400, 100));

        Assert.NotEqual(before, rebuilt);
        Assert.NotEqual(before, resized);
    }

    /// <summary>An unchanged build keeps its cache, which is the point of having one.</summary>
    [Fact]
    public void An_unchanged_build_keeps_the_same_stamp()
    {
        string[] paths = ["/app/Tosh.Cli.dll", "/app/Tosh.Tui.dll"];

        Assert.Equal(
            DotNetTypeResolver.ApplicationStamp(paths, "/app/", _ => Stat(1000, 100)),
            DotNetTypeResolver.ApplicationStamp(paths, "/app/", _ => Stat(1000, 100)));
    }

    /// <summary>
    /// The order the trusted-platform list happens to be in does not change the stamp.
    /// </summary>
    /// <remarks>
    /// A stamp that varied with it would mint a new cache file for no reason, and the
    /// directory that found this bug had 7,478 of them already.
    /// </remarks>
    [Fact]
    public void The_order_of_the_list_does_not_change_the_stamp()
        => Assert.Equal(
            DotNetTypeResolver.ApplicationStamp(
                ["/app/a.dll", "/app/b.dll"], "/app/", path => Stat(path.Length, 1)),
            DotNetTypeResolver.ApplicationStamp(
                ["/app/b.dll", "/app/a.dll"], "/app/", path => Stat(path.Length, 1)));

    /// <summary>
    /// Only the application's own assemblies are stamped.
    /// </summary>
    /// <remarks>
    /// The framework's do not change under a running installation and there are two hundred
    /// of them; stamping those would pay two hundred file reads at every start-up to learn
    /// nothing at all.
    /// </remarks>
    [Fact]
    public void The_frameworks_assemblies_are_not_stamped()
    {
        var stamped = new List<string>();

        DotNetTypeResolver.ApplicationStamp(
            ["/usr/share/dotnet/System.Private.CoreLib.dll", "/app/Tosh.Tui.dll"],
            "/app/",
            path =>
            {
                stamped.Add(path);
                return Stat(1, 1);
            });

        Assert.Equal(["/app/Tosh.Tui.dll"], stamped);
    }

    /// <summary>A published single-file binary has nothing to stamp, and needs none.</summary>
    [Fact]
    public void A_layout_with_no_application_paths_stamps_nothing()
        => Assert.Equal(
            string.Empty,
            DotNetTypeResolver.ApplicationStamp(["/usr/share/dotnet/x.dll"], "/app/", _ => Stat(1, 1)));

    /// <summary>A file that cannot be read contributes nothing rather than throwing.</summary>
    [Fact]
    public void A_file_that_cannot_be_read_is_skipped()
        => Assert.Equal(
            string.Empty,
            DotNetTypeResolver.ApplicationStamp(["/app/gone.dll"], "/app/", _ => null));

    /// <summary>The newest files are kept and the rest are named for deletion.</summary>
    [Fact]
    public void Pruning_keeps_the_most_recent_and_drops_the_rest()
    {
        var files = new[]
        {
            ("/c/platform-types-aaa.idx", new DateTime(2026, 1, 3)),
            ("/c/platform-types-aaa.asm", new DateTime(2026, 1, 3)),
            ("/c/platform-types-bbb.idx", new DateTime(2026, 1, 2)),
            ("/c/platform-types-bbb.asm", new DateTime(2026, 1, 2)),
            ("/c/platform-types-ccc.idx", new DateTime(2026, 1, 1)),
            ("/c/platform-types-ccc.asm", new DateTime(2026, 1, 1)),
        };

        var pruned = DotNetTypeResolver.CacheFilesToPrune(files, keep: 1);

        // Both halves of a pair go, and only the newest pair stays.
        Assert.Equal(4, pruned.Count);
        Assert.Contains("/c/platform-types-bbb.idx", pruned);
        Assert.Contains("/c/platform-types-bbb.asm", pruned);
        Assert.Contains("/c/platform-types-ccc.idx", pruned);
        Assert.DoesNotContain("/c/platform-types-aaa.idx", pruned);
    }

    /// <summary>
    /// A pair is as recent as its newer half.
    /// </summary>
    /// <remarks>
    /// The two files are written moments apart, and a pair being written right now must not
    /// look like an old one because its first half has an older timestamp.
    /// </remarks>
    [Fact]
    public void A_pair_is_as_recent_as_its_newer_half()
    {
        var files = new[]
        {
            ("/c/platform-types-new.asm", new DateTime(2020, 1, 1)),
            ("/c/platform-types-new.idx", new DateTime(2026, 6, 1)),
            ("/c/platform-types-old.idx", new DateTime(2024, 1, 1)),
            ("/c/platform-types-old.asm", new DateTime(2024, 1, 1)),
        };

        Assert.DoesNotContain(
            "/c/platform-types-new.idx",
            DotNetTypeResolver.CacheFilesToPrune(files, keep: 1));
    }

    [Fact]
    public void Nothing_is_pruned_when_there_is_room()
        => Assert.Empty(DotNetTypeResolver.CacheFilesToPrune(
            [("/c/platform-types-a.idx", DateTime.UnixEpoch)], keep: 12));
}
