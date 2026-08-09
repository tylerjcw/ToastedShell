using System.Diagnostics;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// A resolved type name is not resolved twice — <c>TS-P1-42</c>.
/// </summary>
/// <remarks>
/// <para>
/// <c>DotNetTypeResolver</c> cached failures and nothing else, so every success repeated
/// the whole search. Measured from script, a static call cost ~170ms:
/// <c>Path.GetRelativePath</c> 50 times took 8,337ms and <c>String.Join</c> 12,819ms,
/// against 2ms for 50 <em>instance</em> calls — a ~4,000x gap between calling a method on
/// a value and calling one on a type. After the cache: 113ms and 189ms.
/// </para>
/// <para>
/// The cost is structural rather than one slow lookup. Resolving an unqualified name
/// walks a dozen implicit usings; each miss enters <c>TryResolveDirect</c>, whose
/// nested-type fallback recurses on the parent name, so
/// <c>System.Collections.Generic.Path</c> becomes <c>System.Collections.Generic</c>
/// becomes <c>System.Collections</c> becomes <c>System</c> — calling
/// <c>AppDomain.CurrentDomain.GetAssemblies()</c> twice per level.
/// </para>
/// <para>
/// Found porting a Python script to ToastScript, and invisible to the rest of the suite:
/// 4,362 tests drive the runtime from C# and never pay a resolution cost. Same blind spot
/// as <c>TS-P1-30</c>, where a test process has no TTY.
/// </para>
/// </remarks>
public sealed class TypeResolutionCacheTests
{
    [Fact]
    public void A_repeated_resolution_is_much_cheaper_than_the_first()
    {
        // Deliberately a ratio, not a wall-clock budget: an absolute threshold would be a
        // flake on a loaded machine, and the defect was three orders of magnitude.
        var resolver = new DotNetTypeResolver();

        var cold = Stopwatch.StartNew();
        Assert.NotNull(resolver.Resolve("Path"));
        cold.Stop();

        var warm = Stopwatch.StartNew();
        for (var i = 0; i < 200; i++)
        {
            Assert.NotNull(resolver.Resolve("Path"));
        }
        warm.Stop();

        // 200 cached lookups must cost less than one uncached one. That holds with room to
        // spare when the cache works and cannot hold at all when it does not.
        Assert.True(
            warm.Elapsed < cold.Elapsed,
            $"200 warm resolutions took {warm.Elapsed.TotalMilliseconds:F1}ms against "
            + $"{cold.Elapsed.TotalMilliseconds:F1}ms for one cold one — the cache is not being hit.");
    }

    [Fact]
    public void The_cached_answer_is_the_same_answer()
    {
        // A fast wrong answer is worse than a slow right one.
        var resolver = new DotNetTypeResolver();

        Assert.Equal(typeof(System.IO.Path), resolver.Resolve("Path"));
        Assert.Equal(typeof(System.IO.Path), resolver.Resolve("Path"));
        Assert.Equal(typeof(System.IO.Path), resolver.Resolve("System.IO.Path"));
        Assert.Equal(typeof(string), resolver.Resolve("string"));
        Assert.Equal(typeof(string), resolver.Resolve("string"));
    }

    [Fact]
    public void A_failure_stays_a_failure_and_is_still_null()
    {
        var resolver = new DotNetTypeResolver();

        Assert.Null(resolver.Resolve("NoSuchTypeAnywhereInThisProcess"));
        Assert.Null(resolver.Resolve("NoSuchTypeAnywhereInThisProcess"));
    }

    // ── Invalidation: the half a cache gets wrong ──────────────────────────────

    // `SpinLock` rather than `Complex` for these. TS-P2-66 measured that *no* simple name
    // resolves through the imports alone — the direct scan finds something for all 16,727
    // of them — so "the import is what makes it resolve" is not a case that exists. What
    // an import changes is *which* of two types wins: without `System.Threading`,
    // `SpinLock` is a private nested field type inside `ReaderWriterLockSlim`. My first
    // draft of these three used `Complex`, which resolves to `System.Numerics.Complex`
    // with or without the import, and all three failed on the premise rather than the code.

    [Fact]
    public void Adding_a_using_invalidates_what_was_cached_without_it()
    {
        // The case that makes a static cache incorrect: two resolvers with different
        // `using` sets legitimately disagree about the same name, which is the whole
        // premise of TS-P2-66. Caching before the import must not freeze the old answer.
        var resolver = new DotNetTypeResolver(includeDefaultUsings: false);

        var before = resolver.Resolve("SpinLock");
        resolver.AddUsing("System.Threading");
        var after = resolver.Resolve("SpinLock");

        Assert.Equal("System.Threading.SpinLock", after?.FullName);
        Assert.NotEqual(before, after);
    }

    [Fact]
    public void Removing_a_using_invalidates_too()
    {
        var resolver = new DotNetTypeResolver(includeDefaultUsings: false);
        resolver.AddUsing("System.Threading");

        Assert.Equal("System.Threading.SpinLock", resolver.Resolve("SpinLock")?.FullName);

        Assert.True(resolver.RemoveUsing("System.Threading"));
        Assert.NotEqual("System.Threading.SpinLock", resolver.Resolve("SpinLock")?.FullName);
    }

    [Fact]
    public void Adding_an_alias_invalidates_too()
    {
        var resolver = new DotNetTypeResolver(includeDefaultUsings: false);

        Assert.Null(resolver.Resolve("Sb"));
        resolver.AddAlias("Sb", "System.Text.StringBuilder");

        Assert.Equal(typeof(System.Text.StringBuilder), resolver.Resolve("Sb"));
    }

    [Fact]
    public void Two_resolvers_do_not_share_a_cache()
    {
        // Per-instance, so one script's `using` cannot leak into another's resolution.
        var withThreading = new DotNetTypeResolver(includeDefaultUsings: false);
        withThreading.AddUsing("System.Threading");

        var without = new DotNetTypeResolver(includeDefaultUsings: false);

        Assert.Equal("System.Threading.SpinLock", withThreading.Resolve("SpinLock")?.FullName);
        Assert.NotEqual("System.Threading.SpinLock", without.Resolve("SpinLock")?.FullName);
    }

    [Fact]
    public void Precedence_from_TS_P2_66_survives_caching()
    {
        // The reorder this cache sits in front of. Cached or not, an import still outranks
        // an incidental nested match, both on the first call and the second.
        var resolver = new DotNetTypeResolver();

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal("System.Threading.SpinLock", resolver.Resolve("SpinLock")?.FullName);
            Assert.Equal("System.Numerics.BigInteger", resolver.Resolve("BigInteger")?.FullName);
            Assert.Equal("System.Text.StringBuilder", resolver.Resolve("StringBuilder")?.FullName);
        }
    }

    [Fact]
    public void A_constructed_generic_caches_by_its_whole_name()
    {
        var resolver = new DotNetTypeResolver();

        Assert.Equal(typeof(List<int>), resolver.Resolve("list<int>"));
        Assert.Equal(typeof(List<string>), resolver.Resolve("list<string>"));
        Assert.Equal(typeof(List<int>), resolver.Resolve("list<int>"));
    }
}
