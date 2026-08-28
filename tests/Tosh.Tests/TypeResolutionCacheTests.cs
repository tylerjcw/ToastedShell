using System.Reflection;
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
    public void A_resolution_is_remembered_rather_than_repeated()
    {
        // Structural, not timed. This was first written as "200 warm resolutions must
        // cost less than one cold one", which passed alone and failed under full-suite
        // load — a wall-clock comparison measures the machine as much as the code. The
        // speedup is real and recorded in the commit (8,337ms to 113ms for 50 calls);
        // what belongs in the suite is the mechanism, which is observable directly.
        var resolver = new DotNetTypeResolver();
        var field = typeof(DotNetTypeResolver).GetField(
            "_resolutionCache",
            BindingFlags.NonPublic | BindingFlags.Instance)!;

        var cache = (System.Collections.IDictionary)field.GetValue(resolver)!;
        Assert.Equal(0, cache.Count);

        Assert.NotNull(resolver.Resolve("Path"));
        var afterFirst = cache.Count;
        Assert.True(afterFirst > 0, "resolving a name did not populate the cache");

        for (var i = 0; i < 50; i++)
        {
            Assert.NotNull(resolver.Resolve("Path"));
        }

        Assert.Equal(afterFirst, cache.Count);
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

    // These need a name where an import changes *which* type wins, because TS-P2-66 measured
    // that no simple name resolves through the imports alone — the direct scan finds something
    // for all 16,727. A first draft used `Complex`, which resolves to `System.Numerics.Complex`
    // with or without the import, and failed on the premise rather than the code.
    //
    // `SpinLock` worked for exactly one reason: without `System.Threading` it resolved to a
    // private nested field type inside `ReaderWriterLockSlim`. `TOAST-0078` stopped the
    // resolver returning types a script cannot legally name, so that premise is gone and
    // `SpinLock` now resolves to the public `System.Threading.SpinLock` either way.
    //
    // `Timer` replaces it, and needs no accident: `System.Threading.Timer` and
    // `System.Timers.Timer` are both public, so which one wins is genuinely what the import
    // decides.

    [Fact]
    public void Adding_a_using_invalidates_what_was_cached_without_it()
    {
        // The case that makes a static cache incorrect: two resolvers with different
        // `using` sets legitimately disagree about the same name, which is the whole
        // premise of TS-P2-66. Caching before the import must not freeze the old answer.
        var resolver = new DotNetTypeResolver(includeDefaultUsings: false);

        var before = resolver.Resolve("Timer");
        resolver.AddUsing("System.Timers");
        var after = resolver.Resolve("Timer");

        Assert.Equal("System.Timers.Timer", after?.FullName);
        Assert.NotEqual(before, after);
    }

    [Fact]
    public void Removing_a_using_invalidates_too()
    {
        var resolver = new DotNetTypeResolver(includeDefaultUsings: false);
        resolver.AddUsing("System.Timers");

        Assert.Equal("System.Timers.Timer", resolver.Resolve("Timer")?.FullName);

        Assert.True(resolver.RemoveUsing("System.Timers"));
        Assert.NotEqual("System.Timers.Timer", resolver.Resolve("Timer")?.FullName);
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
        var withTimers = new DotNetTypeResolver(includeDefaultUsings: false);
        withTimers.AddUsing("System.Timers");

        var without = new DotNetTypeResolver(includeDefaultUsings: false);

        Assert.Equal("System.Timers.Timer", withTimers.Resolve("Timer")?.FullName);
        Assert.NotEqual("System.Timers.Timer", without.Resolve("Timer")?.FullName);
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
